using AdminConsole.Domain.Abstractions;
using AdminConsole.Domain.Events;
using AdminConsole.Domain.Models;
using AdminConsole.Infrastructure.Configuration;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AdminConsole.Infrastructure.Monitoring;

/// <summary>
/// Tracks Online↔Offline transitions for each server.
/// Handles PingBatchResultOccurred (MediatR notification instead of
/// IRecipient&lt;PingBatchResultMessage&gt;). Persists incidents via
/// IDowntimeRepository (EF Core, Phase 2). Publishes UptimeUpdatedOccurred
/// on every change.
///
/// T4.3: anti-flapping (Pending→Confirmed) and reconciliation logic carried
/// over UNCHANGED. Debounced ScheduleSave()/dirty-months (the file-based
/// optimization that batched writes before File.Move) has been removed —
/// with EF Core every change is written immediately via its own
/// await UpsertAsync/DeleteAsync call right AFTER leaving the _lock (the
/// lock itself still wraps the in-memory mutations 1:1, as before; the only
/// difference is that the write now happens AFTER the lock — previously a
/// debounced Task.Run, now a direct await). This also eliminates the whole
/// "lost the last 500ms before shutdown" class of bugs — a final-flush
/// StopAsync is no longer needed, every change is already on disk (in the
/// DB) at the moment it's made.
/// </summary>
public sealed class UptimeTrackerService(
    IMediator                     mediator,
    IServiceScopeFactory          scopeFactory,
    ILogger<UptimeTrackerService> logger,
    MaintenanceService            maintenance,
    IOptions<List<ServerEntry>>   servers,
    IOptions<MonitoringSettings>  settings)
    : BackgroundService,
        INotificationHandler<PingBatchResultOccurred>,
        INotificationHandler<MaintenanceChangedOccurred>
{
    private readonly IReadOnlyList<ServerEntry> _servers  = servers.Value.AsReadOnly();
    private readonly MonitoringSettings         _settings = settings.Value;

    // IServiceScopeFactory instead of injecting IDowntimeRepository directly —
    // the repository is Scoped, the service is Singleton (same pattern as MaintenanceService).
    private async Task<T> WithRepositoryAsync<T>(Func<IDowntimeRepository, Task<T>> action)
    {
        using var scope = scopeFactory.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<IDowntimeRepository>());
    }

    private Task WithRepositoryAsync(Func<IDowntimeRepository, Task> action) =>
        WithRepositoryAsync(async r => { await action(r); return true; });

    /// Current status of each IP (used to detect transitions)
    private readonly Dictionary<string, PingStatus> _lastStatus = new();

    /// <summary>
    /// IPs for which the FIRST real (not Checking/Unknown) ping result of
    /// this session has already arrived. Needed for reconciliation at
    /// startup: right after a restart _lastStatus is empty, so the usual
    /// "prev == Offline" check never fires for a server that recovered
    /// while the app was down — we have to check once (just once, after
    /// that the normal prev==Offline logic already works correctly)
    /// directly against the persisted _records for an "orphaned" open
    /// incident for this IP.
    /// </summary>
    private readonly HashSet<string> _reconciledIps = new();

    // All incidents in memory (current session + loaded from DB)
    private readonly List<DowntimeRecord> _records = new();
    private readonly object               _lock    = new();

    /// <summary>
    /// Servers that are currently Offline but haven't "matured" to
    /// MinIncidentDurationSeconds yet. Lives purely in memory — no
    /// DowntimeRecord, no upsert, no PublishSnapshot until the incident is
    /// confirmed and moved into _records. Lets us avoid unnecessary I/O
    /// and UI flicker entirely for short network "blips".
    /// </summary>
    private readonly Dictionary<string, PendingOffline> _pendingOffline = new();

    private readonly record struct PendingOffline(
        DateTimeOffset FellAt, string ServerName, string Group);

    private const string LogSource = "UptimeTracker";

    // ── Lifecycle: guaranteed load BEFORE the next services start ───────────

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await LoadFromDbAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Audit Zone 1 (2026-08-22): previously this had NO try/catch at
        // all — if PublishSnapshotAsync threw (e.g. a DB problem), the app
        // wouldn't come up at all (BackgroundServiceExceptionBehavior).
        // Transition tracking itself (Handle(PingBatchResultOccurred)) is
        // unaffected — that's a separate call path through MediatR, not
        // ExecuteAsync.
        try
        {
            await PublishSnapshotAsync(stoppingToken);
            logger.LogInformation("UptimeTrackerService started.");
            await mediator.Publish(AppLogEntryOccurred.Info(LogSource,
                "Uptime tracker started — tracking Online/Offline transitions."), stoppingToken);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "UptimeTrackerService: startup error — initial snapshot not published.");
        }
    }

    // ── INotificationHandler<MaintenanceChangedOccurred> ───────────────────

    public async Task Handle(MaintenanceChangedOccurred notification, CancellationToken ct)
    {
        switch (notification.Action)
        {
            case MaintenanceAction.Started:
                await HandleMaintenanceStartedAsync(notification.Window, ct);
                break;
            case MaintenanceAction.Ended:
                HandleMaintenanceEnded(notification.Window);
                break;
        }
    }

    // Bug fix (2026-08-23, audit Finding 7.1): same reasoning as
    // HandlePingBatchResultAsync above — this is called from
    // Handle(MaintenanceChangedOccurred), which had the identical gap.
    private async Task HandleMaintenanceStartedAsync(MaintenanceWindow window, CancellationToken ct)
    {
        try
        {
            await HandleMaintenanceStartedInternalAsync(window, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "UptimeTrackerService: HandleMaintenanceStartedAsync failed for {Window}.", window.DisplayName);
            try
            {
                await mediator.Publish(AppLogEntryOccurred.Error(LogSource,
                    $"Uptime tracker: failed to process Maintenance start for {window.DisplayName} — {ex.GetType().Name}: {ex.Message}."), CancellationToken.None);
            }
            catch { /* best effort */ }
        }
    }

    private async Task HandleMaintenanceStartedInternalAsync(MaintenanceWindow window, CancellationToken ct)
    {
        var touched = new List<DowntimeRecord>();

        lock (_lock)
        {
            var affected = window.TargetGroup is not null
                ? _records.Where(r => r.ServerGroup.Equals(window.TargetGroup,
                    StringComparison.OrdinalIgnoreCase) && !r.IsResolved)
                : _records.Where(r => r.ServerIp == window.ServerIp && !r.IsResolved);

            foreach (var record in affected)
            {
                record.RecoveredAt        = DateTimeOffset.Now;
                record.ClosedByMaintenance = true;
                touched.Add(record);

                _lastStatus[record.ServerIp] = PingStatus.Unknown;
            }

            var pendingKeysToRemove = window.TargetGroup is not null
                ? _pendingOffline.Where(kv => kv.Value.Group.Equals(
                        window.TargetGroup, StringComparison.OrdinalIgnoreCase))
                    .Select(kv => kv.Key).ToList()
                : (_pendingOffline.ContainsKey(window.ServerIp!)
                    ? [window.ServerIp!]
                    : []);

            foreach (var key in pendingKeysToRemove)
                _pendingOffline.Remove(key);
        }

        if (touched.Count == 0) return;

        await WithRepositoryAsync(async r =>
        {
            foreach (var record in touched)
                await r.UpsertAsync(record, ct);
        });

        await PublishSnapshotAsync(ct);

        await mediator.Publish(AppLogEntryOccurred.Info(LogSource,
            $"Open incidents for {window.DisplayName} closed due to Maintenance Mode."), ct);
    }

    /// <summary>
    /// FIX (carried over unchanged): without this, _lastStatus[ip] stayed
    /// Offline forever if the server didn't come back up by the end of the
    /// window — no subsequent Offline ping was ever treated as a "new
    /// transition" (prev already equaled Offline), so a DowntimeRecord was
    /// never created for the period AFTER the window.
    ///
    /// We reset specifically to Online (not Unknown, as in
    /// PingMonitorService) — in THIS service a Unknown/Checking → Offline
    /// transition intentionally does not create a _pendingOffline entry
    /// (the "startup noise" filter), so Unknown would reproduce the same
    /// bug. Online → Offline is the normal, already-tested branch: the
    /// next real Offline ping correctly "falls" into _pendingOffline with
    /// FellAt = the moment it was detected. If the server is already
    /// online — this is a safe no-op.
    /// </summary>
    private void HandleMaintenanceEnded(MaintenanceWindow window)
    {
        var affectedIps = window.TargetGroup is not null
            ? _servers.Where(s => s.Group.Equals(window.TargetGroup,
                    StringComparison.OrdinalIgnoreCase))
                .Select(s => s.IP)
            : window.ServerIp is not null
                ? [window.ServerIp]
                : Array.Empty<string>();

        lock (_lock)
        {
            foreach (var ip in affectedIps)
                _lastStatus[ip] = PingStatus.Online;
        }
    }

    // ── INotificationHandler<PingBatchResultOccurred> ──────────────────────

    // Bug fix (2026-08-23, audit Finding 7.1): this handler used to have no
    // exception protection at all — an unhandled DB failure here propagated
    // back through PingMonitorService.PingServersAsync into its loop guard,
    // which cancels BOTH the main and recovery ping loops together by
    // design (a subscriber crashing its publisher). In a pub/sub MediatR
    // handler, a subscriber must never take down the publisher this way —
    // one missed write to the downtime table is a much smaller problem than
    // permanently halting all ping/uptime monitoring until a service
    // restart. OperationCanceledException still propagates (normal
    // shutdown); anything else is logged and swallowed.
    public async Task Handle(PingBatchResultOccurred notification, CancellationToken ct)
    {
        try
        {
            await HandlePingBatchResultAsync(notification, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "UptimeTrackerService: Handle(PingBatchResultOccurred) failed.");
            try
            {
                await mediator.Publish(AppLogEntryOccurred.Error(LogSource,
                    $"Uptime tracker: failed to process a ping batch — {ex.GetType().Name}: {ex.Message}."), CancellationToken.None);
            }
            catch { /* best effort — the ILogger call above already recorded what matters */ }
        }
    }

    private async Task HandlePingBatchResultAsync(PingBatchResultOccurred notification, CancellationToken ct)
    {
        bool changed = false;
        var touched = new List<DowntimeRecord>();
        var reconciledLogs = new List<(string ServerName, string ServerIp, DateTimeOffset FellAt)>();

        lock (_lock)
        {
            foreach (var result in notification.Payload.Results)
            {
                if (result.Status is PingStatus.Unknown or PingStatus.Checking)
                {
                    _lastStatus[result.IP] = result.Status;
                    continue;
                }

                _lastStatus.TryGetValue(result.IP, out var prev);

                // Add() returns true if this is the FIRST time we're seeing
                // this IP with a real (not Checking/Unknown) status this
                // session — this is exactly the moment that needs
                // reconciliation against the DB (see the Online branch below).
                bool isFirstRealStatusThisSession = _reconciledIps.Add(result.IP);

                if (result.Status == PingStatus.Offline)
                {
                    bool underMaintenance = maintenance.IsUnderMaintenance(result.IP, result.Group);

                    if (prev != PingStatus.Offline
                        && prev is not PingStatus.Unknown and not PingStatus.Checking
                        && !underMaintenance)
                    {
                        // Fresh drop — do NOT write a DowntimeRecord right away.
                        // Put it in pending and wait MinIncidentDurationSeconds
                        // before it becomes an "official" incident.
                        _pendingOffline[result.IP] =
                            new PendingOffline(DateTimeOffset.Now, result.Name, result.Group);
                    }
                    else if (!underMaintenance &&
                             _pendingOffline.TryGetValue(result.IP, out var pending))
                    {
                        // Server is still Offline — check whether the threshold has passed.
                        var elapsed = DateTimeOffset.Now - pending.FellAt;
                        if (_settings.MinIncidentDurationSeconds <= 0
                            || elapsed.TotalSeconds >= _settings.MinIncidentDurationSeconds)
                        {
                            // The incident has "matured" — only now do we
                            // create the record, write it to the DB, and
                            // show it in the UI. FellAt stays the real drop
                            // time, not the moment of promotion.
                            var record = new DowntimeRecord
                            {
                                ServerName  = pending.ServerName,
                                ServerIp    = result.IP,
                                ServerGroup = pending.Group,
                                FellAt      = pending.FellAt
                            };
                            _records.Insert(0, record);
                            touched.Add(record);
                            _pendingOffline.Remove(result.IP);
                            changed = true;
                        }
                    }
                }
                else if (result.Status == PingStatus.Online)
                {
                    if (prev == PingStatus.Offline)
                    {
                        // Normal, already time-tested path: the server went
                        // down and came back up while the app was RUNNING —
                        // _lastStatus correctly tracked both transitions
                        // this session.
                        if (!_pendingOffline.Remove(result.IP))
                        {
                            var open = _records.FirstOrDefault(
                                r => r.ServerIp == result.IP && !r.IsResolved);

                            if (open is not null)
                            {
                                open.RecoveredAt = DateTimeOffset.Now;
                                touched.Add(open);
                                changed = true;
                            }
                        }
                    }
                    else if (isFirstRealStatusThisSession)
                    {
                        // FIX: the first real ping for this IP this
                        // session, where prev is NOT Offline (because
                        // _lastStatus is empty right after a restart — the
                        // usual check above would never fire). We
                        // reconcile directly against the DB (already
                        // loaded into _records at startup): if there's an
                        // open incident there for this IP, the server
                        // clearly recovered while the app was down.
                        var open = _records.FirstOrDefault(
                            r => r.ServerIp == result.IP && !r.IsResolved);

                        if (open is not null)
                        {
                            open.RecoveredAt = DateTimeOffset.Now;
                            touched.Add(open);
                            changed = true;

                            reconciledLogs.Add((open.ServerName, open.ServerIp, open.FellAt));
                        }
                    }
                }

                _lastStatus[result.IP] = result.Status;
            }
        }

        if (touched.Count > 0)
            await WithRepositoryAsync(async r =>
            {
                foreach (var record in touched)
                    await r.UpsertAsync(record, ct);
            });

        foreach (var (name, ip, fellAt) in reconciledLogs)
            await mediator.Publish(AppLogEntryOccurred.Info(LogSource,
                $"{name} ({ip}) is already ONLINE after the app restarted — " +
                $"closed the incident that started at {fellAt:dd.MM HH:mm}."), ct);

        if (!changed) return;

        await PublishSnapshotAsync(ct);
    }

    // ── Public API for UptimeViewModel/React (Phase 6) ───────────────────────

    /// <summary>
    /// FIX (carried over unchanged): returns DEEP copies, not references to
    /// the live objects in _records. DowntimeRecord.RecoveredAt/
    /// ClosedByMaintenance are mutated from a background thread in
    /// Handle(PingBatchResultOccurred) with no coordination with whoever
    /// reads the snapshot outside of _lock. SlaReportService.Generate()
    /// reads the same record's ClippedDuration multiple times independently
    /// — without freezing a snapshot, these calls could see different
    /// values for the same incident within a single report.
    /// </summary>
    public IReadOnlyList<DowntimeRecord> GetSnapshot()
    {
        lock (_lock) return _records.Select(CloneRecord).ToList();
    }

    private static DowntimeRecord CloneRecord(DowntimeRecord r) => new()
    {
        ServerName          = r.ServerName,
        ServerIp            = r.ServerIp,
        ServerGroup         = r.ServerGroup,
        FellAt              = r.FellAt,
        RecoveredAt         = r.RecoveredAt,
        ClosedByMaintenance = r.ClosedByMaintenance
    };

    /// <summary>
    /// Removes a single record from memory and the DB.
    /// If the record is active (!IsResolved) — resets _lastStatus[IP] to
    /// Online, so the tracker correctly tracks the next transition for this
    /// server. Called from the future Uptime API controller (Phase 6).
    /// </summary>
    public async Task DeleteRecordAsync(DowntimeRecord record, CancellationToken ct = default)
    {
        DowntimeRecord? target;

        lock (_lock)
        {
            target = _records.FirstOrDefault(r =>
                r.ServerIp == record.ServerIp && r.FellAt == record.FellAt);

            if (target is null)
            {
                logger.LogWarning(
                    "DeleteRecord: record {Server} ({Ip}) / {FellAt} not found — may already be deleted.",
                    record.ServerName, record.ServerIp, record.FellAt);
                return;
            }

            if (!target.IsResolved && _lastStatus.ContainsKey(target.ServerIp))
                _lastStatus[target.ServerIp] = PingStatus.Online;

            _records.Remove(target);
        }

        await WithRepositoryAsync(r => r.DeleteAsync(target.ServerIp, target.FellAt, ct));
        await PublishSnapshotAsync(ct);

        await mediator.Publish(AppLogEntryOccurred.Info(LogSource,
            $"Incident deleted manually: {record.ServerName} ({record.ServerIp}), " +
            $"fell at {record.FellAt:dd.MM HH:mm:ss}."), ct);
    }

    public async Task ClearAllResolvedAsync(CancellationToken ct = default)
    {
        int removedInMemory;
        lock (_lock)
        {
            removedInMemory = _records.RemoveAll(r => r.IsResolved);
        }

        if (removedInMemory == 0) return;

        int removed = await WithRepositoryAsync(r => r.DeleteAllResolvedAsync(ct));
        await PublishSnapshotAsync(ct);

        await mediator.Publish(AppLogEntryOccurred.Info(LogSource,
            $"Cleared {removed} resolved incident(s) from history."), ct);
    }

    // ── Persistence ───────────────────────────────────────────────────────────

    private async Task LoadFromDbAsync(CancellationToken ct)
    {
        try
        {
            var loaded = await WithRepositoryAsync(r => r.LoadAllAsync(ct));

            lock (_lock)
            {
                _records.AddRange(loaded);
                _records.Sort((a, b) => b.FellAt.CompareTo(a.FellAt));
            }

            logger.LogInformation(
                "UptimeTrackerService: loaded {Count} record(s).", loaded.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "UptimeTrackerService: error loading from DB.");
        }
    }

    private async Task PublishSnapshotAsync(CancellationToken ct)
    {
        IReadOnlyList<DowntimeRecord> snapshot;
        lock (_lock) snapshot = _records.Select(CloneRecord).ToList();
        await mediator.Publish(new UptimeUpdatedOccurred(snapshot), ct);
    }
}
