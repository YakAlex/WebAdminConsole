using AdminConsole.Domain.Abstractions;
using AdminConsole.Domain.Events;
using AdminConsole.Domain.Models;
using AdminConsole.Infrastructure.Configuration;
using Hangfire;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AdminConsole.Infrastructure.Monitoring;

/// <summary>
/// Polls the configured BackupChecks (FileAge: age + size against a
/// rolling baseline, Full/Diff handled separately). The two-stage check
/// (Stage A/B) is delegated to BackupCheckEvaluator — this job is only
/// responsible for: the cycle, anti-flapping (confirming state only after
/// N identical "raw" results in a row), persistence via
/// IBackupStateRepository, and suppressing alerts during Maintenance
/// Windows.
///
/// T4.5: Hangfire recurring job, NOT a BackgroundService/Singleton (per the
/// Hangfire vs BackgroundService rule — a discrete job-like task, interval
/// measured in minutes, benefits from Hangfire's retry/dashboard
/// visibility).
///
/// Since Hangfire creates a new job instance on every run (Scoped/Transient,
/// not a long-lived Singleton), the old _stateLock + ConcurrentDictionary
/// _states (protecting concurrent reads via GetSnapshot() from the UI
/// thread while the background loop mutates state) are NO LONGER NEEDED —
/// within a single run the job processes definitions strictly sequentially
/// (as before), and between runs state is persisted through the repository
/// anyway (BackupCheckState already had all the anti-flapping counters as
/// persistent fields since Phase 2). This is a direct consequence of T4.5
/// (the service is explicitly NOT staying a Singleton), not a deviation
/// from the "locks carry over 1:1" rule.
/// </summary>
public sealed class BackupMonitorJob(
    IMediator                              mediator,
    ILogger<BackupMonitorJob>              logger,
    IOptions<MonitoringSettings>           settings,
    IOptions<List<BackupCheckDefinition>>  backupChecks,
    IOptions<List<ServerEntry>>            servers,
    MaintenanceService                     maintenance,
    BackupCheckEvaluator                   evaluator,
    IBackupStateRepository                 repository,
    IAppSettingsRepository                 appSettings)
{
    private readonly MonitoringSettings _settings = settings.Value;
    private readonly IReadOnlyList<BackupCheckDefinition> _definitions = backupChecks.Value.AsReadOnly();

    private readonly IReadOnlyDictionary<string, ServerEntry> _serverLookup = servers.Value
        .GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

    private const string LogSource         = "BackupMonitor";
    private const int    MaxHistorySamples = 14;

    /// <summary>How many cycles in a row Unknown, before sending a warning once (no spam).</summary>
    private const int UnknownEscalationThreshold = 3;

    /// <summary>
    /// Entry point for RecurringJob.AddOrUpdate (Program.cs, T4.5).
    /// Audit Zone 1, Finding #7 (2026-08-22): BackupChecks reads files over
    /// UNC paths (Zone 3 — no guaranteed timeout), so a single run could
    /// theoretically take longer than BackupPollIntervalMinutes. Without
    /// this attribute Hangfire would by default be able to start the next
    /// cycle in parallel with a still-unfinished previous one — both would
    /// write to the same BackupCheckState table at the same time.
    /// timeoutInSeconds=10: if the previous run is still holding the lock
    /// after 10s of waiting — this run is simply skipped (doesn't wait or
    /// fail), the next scheduled run will try again.
    /// </summary>
    [DisableConcurrentExecution(timeoutInSeconds: 10)]
    public async Task RunAsync(CancellationToken ct = default)
    {
        if (_definitions.Count == 0)
        {
            logger.LogInformation("BackupMonitorJob: no BackupChecks configured — skipping cycle.");
            return;
        }

        var currentAppSettings = await appSettings.GetAsync(ct);
        if (!currentAppSettings.BackupMonitoringEnabled)
        {
            logger.LogInformation("BackupMonitorJob: backup monitoring disabled in Settings — skipping cycle.");
            return;
        }

        foreach (var def in _definitions)
        {
            string searchKey = string.IsNullOrWhiteSpace(def.Host) ? def.Name : def.Host;
            if (!_serverLookup.ContainsKey(searchKey))
            {
                await mediator.Publish(AppLogEntryOccurred.Warning(LogSource,
                    $"BackupChecks: '{def.Name}' was not found among Servers in appsettings.json — " +
                    $"Maintenance suppression will not work for this entry."), ct);
            }
        }

        var existingStates = (await repository.LoadAllAsync(ct))
            .ToDictionary(s => StateKey(s.Name, s.Kind));

        var updatedStates = new List<BackupCheckState>();

        foreach (var def in _definitions)
        {
            updatedStates.Add(await CheckKindSafeAsync(def, BackupKind.Full, existingStates, ct));

            if (!string.IsNullOrWhiteSpace(def.DiffPattern))
                updatedStates.Add(await CheckKindSafeAsync(def, BackupKind.Diff, existingStates, ct));
        }

        foreach (var state in updatedStates)
            await repository.UpsertAsync(state, ct);

        var validKeys = updatedStates.Select(s => StateKey(s.Name, s.Kind)).ToHashSet();
        int removed = await repository.DeleteWhereKeyNotInAsync(validKeys, ct);
        if (removed > 0)
        {
            await mediator.Publish(AppLogEntryOccurred.Info(LogSource,
                $"Removed {removed} stale record(s) — no longer present in the BackupChecks configuration."), ct);
        }

        await mediator.Publish(new BackupStatusUpdatedOccurred(updatedStates), ct);
    }

    /// <summary>Wraps CheckKindAsync — one failed check should not stop the whole cycle.</summary>
    private async Task<BackupCheckState> CheckKindSafeAsync(
        BackupCheckDefinition def, BackupKind kind,
        Dictionary<string, BackupCheckState> existingStates, CancellationToken ct)
    {
        try
        {
            return await CheckKindAsync(def, kind, existingStates, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "BackupMonitorJob: unexpected error for {Server}/{Kind}",
                def.Name, kind);

            return existingStates.TryGetValue(StateKey(def.Name, kind), out var fallback)
                ? fallback
                : new BackupCheckState { Name = def.Name, Host = def.Host, Kind = kind };
        }
    }

    private async Task<BackupCheckState> CheckKindAsync(
        BackupCheckDefinition def, BackupKind kind,
        Dictionary<string, BackupCheckState> existingStates, CancellationToken ct)
    {
        var key = StateKey(def.Name, kind);
        if (!existingStates.TryGetValue(key, out var state))
        {
            state = new BackupCheckState { Name = def.Name, Host = def.Host, Kind = kind };
        }

        var raw = await evaluator
            .EvaluateAsync(def, kind, state.History, ct)
            .ConfigureAwait(false);

        (bool shouldNotify, BackupOutcome previous, BackupOutcome current) transition = default;
        bool crossedUnknownThreshold;

        // ── Unknown streak (separate from the confirmed-state anti-flapping) ──
        state.ConsecutiveUnknownCount = raw.Outcome == BackupOutcome.Unknown
            ? state.ConsecutiveUnknownCount + 1
            : 0;
        crossedUnknownThreshold = state.ConsecutiveUnknownCount == UnknownEscalationThreshold;

        bool neverConfirmedYet = state.LastConfirmedAt is null;

        // ── LastConfirmed* — any NON-Unknown response, regardless of anti-flapping ──
        if (raw.Outcome != BackupOutcome.Unknown)
        {
            state.LastConfirmedAt      = DateTimeOffset.Now;
            state.LastConfirmedOutcome = raw.Outcome;
            state.LastError            = null;
        }
        else
        {
            state.LastError = raw.ErrorMessage;
        }

        // ── History — only when Stage B actually found a file ──
        if (raw.Sample is not null)
        {
            state.History.Add(raw.Sample);
            while (state.History.Count > MaxHistorySamples)
                state.History.RemoveAt(0);
        }

        // ── First confirmation after a restart/first run —
        // confirm immediately, without waiting for MinConsecutiveForAlert.
        // Before the first confirmed result there's no "previous state"
        // for anti-flapping to protect — waiting here would only delay
        // the correct initial status (by up to BackupPollIntervalMinutes
        // × MinConsecutiveForAlert minutes) with no compensating benefit.
        if (neverConfirmedYet && raw.Outcome != BackupOutcome.Unknown)
        {
            var firstPrevious = state.Outcome;
            state.Outcome             = raw.Outcome;
            state.ConsecutiveBadCount = 0;
            state.LastRawOutcome      = null;

            transition = (true, firstPrevious, state.Outcome);
        }
        // ── Anti-flapping: the confirmed Outcome (what the UI/alerts see) ──
        else if (raw.Outcome == state.Outcome)
        {
            state.ConsecutiveBadCount = 0;
            state.LastRawOutcome      = raw.Outcome;
        }
        else
        {
            // Count the streak of IDENTICAL "raw" results in a row that
            // differ from the confirmed Outcome — not just any change in
            // raw.Outcome at all (that was the bug: Ok→Stale→Missing used
            // to confirm the transition even though no raw result repeated
            // twice in a row).
            state.ConsecutiveBadCount = raw.Outcome == state.LastRawOutcome
                ? state.ConsecutiveBadCount + 1
                : 1;
            state.LastRawOutcome = raw.Outcome;

            if (state.ConsecutiveBadCount >= def.MinConsecutiveForAlert)
            {
                var previous = state.Outcome;
                state.Outcome             = raw.Outcome;
                state.ConsecutiveBadCount = 0;
                state.LastRawOutcome      = null;

                transition = (true, previous, state.Outcome);
            }
        }

        if (crossedUnknownThreshold)
        {
            await mediator.Publish(AppLogEntryOccurred.Warning(LogSource,
                $"{def.Name} ({kind}): check has been unavailable for " +
                $"{UnknownEscalationThreshold} cycles in a row."), ct);
        }

        if (transition.shouldNotify)
            await OnConfirmedTransitionAsync(def, kind, transition.previous, transition.current, ct);

        return state;
    }

    /// <summary>Called only on a CONFIRMED state transition (after anti-flapping).</summary>
    private async Task OnConfirmedTransitionAsync(
        BackupCheckDefinition def, BackupKind kind,
        BackupOutcome previous, BackupOutcome current, CancellationToken ct)
    {
        string label = $"{def.Name} ({kind})";

        bool underMaintenance =
            _serverLookup.TryGetValue(def.Host, out var entry) &&
            maintenance.IsUnderMaintenance(entry.IP, entry.Group);

        if (current is BackupOutcome.Stale or BackupOutcome.Missing)
        {
            if (underMaintenance)
            {
                await mediator.Publish(AppLogEntryOccurred.Info(LogSource,
                    $"{label}: transition to {current} suppressed (active Maintenance window)."), ct);
                return;
            }

            await mediator.Publish(AppLogEntryOccurred.Error(LogSource,
                $"{label}: transitioned to {current} (was {previous})."), ct);

            await mediator.Publish(new BackupTransitionOccurred(def.Name, kind, previous, current), ct);
            return;
        }

        if (current == BackupOutcome.SizeWarning)
        {
            await mediator.Publish(AppLogEntryOccurred.Warning(LogSource,
                $"{label}: backup size deviated more than {def.SizeWarningThresholdPct}% from the average."), ct);
            return;
        }

        if (current == BackupOutcome.Unknown)
        {
            await mediator.Publish(AppLogEntryOccurred.Warning(LogSource,
                $"{label}: check is not responding (Unknown), was {previous}."), ct);
            return;
        }

        // current == Ok
        if (previous is BackupOutcome.Stale or BackupOutcome.Missing or BackupOutcome.SizeWarning)
        {
            await mediator.Publish(AppLogEntryOccurred.Success(LogSource,
                $"{label}: recovered (was {previous})."), ct);
        }
    }

    private static string StateKey(string serverName, BackupKind kind) => $"{serverName}|{kind}";
}
