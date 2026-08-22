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

        // Bug fix (2026-08-22, backup service audit): unlike every other
        // monitoring loop in this app (Ping/RDP/Zabbix/Uptime — all
        // explicitly hardened in the 2026-08-22 audit), this method had no
        // top-level protection. Per-server evaluation failures were already
        // isolated by CheckKindSafeAsync, but a transient failure in
        // LoadAllAsync/UpsertAsync/DeleteWhereKeyNotInAsync/Publish (none of
        // which go through CheckKindSafeAsync) would abort the whole cycle
        // with nothing but a Hangfire "Failed" entry — invisible in the
        // in-app Logs UI an admin actually watches. We still rethrow after
        // logging: Hangfire's own automatic retry is the correct recovery
        // mechanism here (this is a Hangfire job, not a BackgroundService —
        // swallowing the exception instead of rethrowing would report a
        // false "success" to Hangfire and skip that retry).
        try
        {
            var currentAppSettings = await appSettings.GetAsync(ct);
            if (!currentAppSettings.BackupMonitoringEnabled)
            {
                logger.LogInformation("BackupMonitorJob: backup monitoring disabled in Settings — skipping cycle.");
                return;
            }

            foreach (var def in _definitions)
            {
                if (!_serverLookup.ContainsKey(ServerLookupKey(def)))
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

            // Bug fix (2026-08-22, backup service audit): BackupStatusUpdatedOccurred's
            // doc comment already claimed a cloned snapshot "the same
            // principle as DowntimeRecord/CloneRecord", but updatedStates
            // itself was published directly — harmless today only because
            // Hangfire hands this job a fresh Scoped instance with no
            // surviving references once RunAsync returns. Cloning here
            // makes the guarantee the doc comment already promised actually
            // true, and costs nothing.
            await mediator.Publish(new BackupStatusUpdatedOccurred(updatedStates.Select(CloneState).ToList()), ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "BackupMonitorJob: cycle failed.");
            await mediator.Publish(AppLogEntryOccurred.Error(LogSource,
                $"Backup monitor: cycle failed — {ex.GetType().Name}: {ex.Message}. " +
                "Hangfire will retry; any states already saved this cycle are kept."), CancellationToken.None);
            throw;
        }
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

        // Bug fix (2026-08-22, backup service audit): computed once here and
        // passed down to both the Unknown-streak warning below and
        // OnConfirmedTransitionAsync, instead of each recomputing its own
        // (inconsistent) lookup.
        bool underMaintenance =
            _serverLookup.TryGetValue(ServerLookupKey(def), out var entry) &&
            maintenance.IsUnderMaintenance(entry.IP, entry.Group);

        var raw = await evaluator
            .EvaluateAsync(def, kind, state.History, ct)
            .ConfigureAwait(false);

        (bool shouldNotify, BackupOutcome previous, BackupOutcome current) transition = default;
        bool crossedUnknownThreshold;

        // ── Unknown streak (separate from the confirmed-state anti-flapping) ──
        state.ConsecutiveUnknownCount = raw.Outcome == BackupOutcome.Unknown
            ? state.ConsecutiveUnknownCount + 1
            : 0;
        // Bug fix (2026-08-22, backup service audit): was `== UnknownEscalationThreshold`
        // — fired exactly once, ever, then went completely silent for as
        // long as the check stayed Unknown (which, for an unreachable share,
        // could be days). Re-fires every UnknownEscalationThreshold cycles
        // thereafter (3, 6, 9, ...) instead of only the first time.
        crossedUnknownThreshold =
            state.ConsecutiveUnknownCount > 0 &&
            state.ConsecutiveUnknownCount % UnknownEscalationThreshold == 0;

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

        // ── History — only a genuinely healthy (Ok) sample ──
        // Bug fix (2026-08-22, backup service audit): this used to add
        // raw.Sample whenever Stage B found a file at all (Ok, SizeWarning,
        // OR Stale), which fed the very anomalies the size check exists to
        // catch back into its own rolling average — a run of corrupted/
        // undersized backups would "poison" the baseline within
        // MinSamplesForBaseline cycles, after which the corrupted size
        // became the new normal and SizeWarning stopped firing. It also
        // meant a long-Stale backup (same file, unchanged size) kept
        // re-adding a duplicate of its own size every single cycle,
        // saturating History and making a subsequent genuine backup look
        // like an anomaly by comparison. Only an Ok result reflects a
        // trustworthy, current, healthy size — that's the only thing this
        // baseline should ever be built from.
        if (raw.Outcome == BackupOutcome.Ok && raw.Sample is not null)
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

        // Bug fix (2026-08-22, backup service audit): this streak means "the
        // check has failed to even REACH its source", which is exactly the
        // kind of noise a Maintenance window is meant to suppress (e.g. the
        // backup share going down as a side effect of a planned reboot).
        if (crossedUnknownThreshold && !underMaintenance)
        {
            await mediator.Publish(AppLogEntryOccurred.Warning(LogSource,
                $"{def.Name} ({kind}): check has been unavailable for " +
                $"{state.ConsecutiveUnknownCount} cycles in a row."), ct);
        }

        if (transition.shouldNotify)
            await OnConfirmedTransitionAsync(def, kind, transition.previous, transition.current, underMaintenance, ct);

        return state;
    }

    /// <summary>
    /// Called only on a CONFIRMED state transition (after anti-flapping).
    /// underMaintenance is computed once by the caller (CheckKindAsync) —
    /// bug fix (2026-08-22, backup service audit): this used to recompute
    /// its own lookup keyed strictly off def.Host, while the startup
    /// validation (and CheckKindSafeAsync's fallback lookup) both accept an
    /// empty Host with Name as a substitute. A BackupCheckDefinition with a
    /// blank Host but a Name matching a real server passed startup
    /// validation silently (no "Maintenance suppression will not work"
    /// warning) yet NEVER actually suppressed alerts during a Maintenance
    /// window, because this lookup could never find it.
    /// </summary>
    private async Task OnConfirmedTransitionAsync(
        BackupCheckDefinition def, BackupKind kind,
        BackupOutcome previous, BackupOutcome current, bool underMaintenance, CancellationToken ct)
    {
        string label = $"{def.Name} ({kind})";

        if (current is BackupOutcome.Stale or BackupOutcome.Missing)
        {
            if (await SuppressIfMaintenanceAsync(label, current, underMaintenance, ct)) return;

            await mediator.Publish(AppLogEntryOccurred.Error(LogSource,
                $"{label}: transitioned to {current} (was {previous})."), ct);

            await mediator.Publish(new BackupTransitionOccurred(def.Name, kind, previous, current), ct);
            return;
        }

        if (current == BackupOutcome.SizeWarning)
        {
            // Bug fix (2026-08-22, backup service audit): previously fired
            // regardless of Maintenance — SizeWarning and Unknown were the
            // only two confirmed transitions NOT suppressed, inconsistent
            // with Stale/Missing right above.
            if (await SuppressIfMaintenanceAsync(label, current, underMaintenance, ct)) return;

            await mediator.Publish(AppLogEntryOccurred.Warning(LogSource,
                $"{label}: backup size deviated more than {def.SizeWarningThresholdPct}% from the average."), ct);
            return;
        }

        if (current == BackupOutcome.Unknown)
        {
            if (await SuppressIfMaintenanceAsync(label, current, underMaintenance, ct)) return;

            await mediator.Publish(AppLogEntryOccurred.Warning(LogSource,
                $"{label}: check is not responding (Unknown), was {previous}."), ct);

            // Bug fix (2026-08-22, backup service audit): a backup check
            // going completely unreachable (share down, permissions lost)
            // used to be the quietest failure mode in the whole system —
            // logged in the in-app Logs UI only, with no Telegram push,
            // while a merely-Stale backup DID push. An admin who isn't
            // actively watching the dashboard would never find out.
            await mediator.Publish(new BackupTransitionOccurred(def.Name, kind, previous, current), ct);
            return;
        }

        // current == Ok
        if (previous is BackupOutcome.Stale or BackupOutcome.Missing or BackupOutcome.SizeWarning or BackupOutcome.Unknown)
        {
            await mediator.Publish(AppLogEntryOccurred.Success(LogSource,
                $"{label}: recovered (was {previous})."), ct);
        }
    }

    /// <summary>Logs a suppression notice and returns true if this transition should be silenced for an active Maintenance window.</summary>
    private async Task<bool> SuppressIfMaintenanceAsync(string label, BackupOutcome current, bool underMaintenance, CancellationToken ct)
    {
        if (!underMaintenance) return false;

        await mediator.Publish(AppLogEntryOccurred.Info(LogSource,
            $"{label}: transition to {current} suppressed (active Maintenance window)."), ct);
        return true;
    }

    private static string StateKey(string serverName, BackupKind kind) => $"{serverName}|{kind}";

    /// <summary>Key into _serverLookup for a given definition — Host, falling back to Name when Host is blank.</summary>
    private static string ServerLookupKey(BackupCheckDefinition def) =>
        string.IsNullOrWhiteSpace(def.Host) ? def.Name : def.Host;

    /// <summary>Deep-enough copy for BackupStatusUpdatedOccurred — see the comment at its publish call site.</summary>
    private static BackupCheckState CloneState(BackupCheckState s) => new()
    {
        Name                    = s.Name,
        Host                    = s.Host,
        Kind                    = s.Kind,
        Outcome                 = s.Outcome,
        LastConfirmedAt         = s.LastConfirmedAt,
        LastConfirmedOutcome    = s.LastConfirmedOutcome,
        ConsecutiveUnknownCount = s.ConsecutiveUnknownCount,
        ConsecutiveBadCount     = s.ConsecutiveBadCount,
        LastRawOutcome          = s.LastRawOutcome,
        LastError               = s.LastError,
        History                 = new List<BackupSample>(s.History),
    };
}
