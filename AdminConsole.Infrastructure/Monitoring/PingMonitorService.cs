using System.Collections.Concurrent;
using System.Net.NetworkInformation;
using AdminConsole.Domain.Events;
using AdminConsole.Domain.Models;
using AdminConsole.Infrastructure.Configuration;
using MediatR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AdminConsole.Infrastructure.Monitoring;

/// <summary>
/// T4.2: BackgroundService, AddSingleton — NO Hangfire (a tight dual-loop
/// cycle on the order of seconds, per the Hangfire vs BackgroundService rule).
///
/// IRecipient&lt;MaintenanceChangedMessage&gt; (registered in the constructor,
/// WeakReferenceMessenger) → INotificationHandler&lt;MaintenanceChangedOccurred&gt;
/// (the class is resolved and invoked through DI, MediatR fan-out).
/// </summary>
public sealed class PingMonitorService(
    IMediator                    mediator,
    ILogger<PingMonitorService>  logger,
    IOptions<MonitoringSettings> settings,
    IOptions<List<ServerEntry>>  servers,
    MaintenanceService           maintenance)
    : BackgroundService, INotificationHandler<MaintenanceChangedOccurred>, IDisposable
{
    private readonly MonitoringSettings         _settings = settings.Value;
    private readonly IReadOnlyList<ServerEntry> _servers  = servers.Value.AsReadOnly();

    // ── Status state ────────────────────────────────────────────────────────

    // Single source of truth for the current status of each IP.
    // ConcurrentDictionary — read and written from both loops concurrently.
    private readonly ConcurrentDictionary<string, PingStatus> _previousStatus = new();

    // ── Throttle ─────────────────────────────────────────────────────────────

    // Main loop: up to 10 parallel pings (15 servers → 10+5)
    private readonly SemaphoreSlim _mainThrottle     = new(10);

    // Recovery loop: a separate throttle with 5 slots.
    // Not shared with the main loop — recovery is never blocked by the main
    // cycle even when all 10 main slots are in use.
    private readonly SemaphoreSlim _recoveryThrottle = new(5);

    /// <summary>
    /// Per-IP lock: if /ping (on-demand from Telegram) and the background
    /// loop (main/recovery loop) try to poll the SAME server at the same
    /// time — without this lock both calls independently read/write
    /// _previousStatus[ip] via GetOrAdd+TryUpdate (CAS), which does NOT
    /// corrupt the dictionary itself, but can duplicate or drop a
    /// Warning/Error log about a status transition due to interleaving
    /// of the two state checks. We serialize exactly at "one server"
    /// granularity — different servers are still pinged fully in parallel
    /// with each other.
    /// </summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _perServerLocks = new();

    private SemaphoreSlim GetServerLock(string ip) =>
        _perServerLocks.GetOrAdd(ip, _ => new SemaphoreSlim(1, 1));

    // Audit fix (2026-08-22): throttling for on-demand /ping (REST + Telegram
    // /ping) — the window matches the main loop (PingIntervalSeconds).
    private readonly OnDemandSnapshotThrottle<IReadOnlyList<PingResult>> _onDemandThrottle =
        new(TimeSpan.FromSeconds(settings.Value.PingIntervalSeconds));

    // ── Constants ────────────────────────────────────────────────────────────

    private const int    PingTimeoutMs        = 2000;
    private const string LogSource            = "PingMonitor";
    private const int    MinOfflineIntervalSec = 5; // guard against a bad appsettings value

    // ── INotificationHandler<MaintenanceChangedOccurred> ────────────────────

    public async Task Handle(MaintenanceChangedOccurred notification, CancellationToken ct)
    {
        if (notification.Action != MaintenanceAction.Ended) return;

        // Reset previousStatus to Unknown for affected servers — the next
        // ping cycle will treat a current Offline status (if the server
        // didn't come back up in time) as a "transition from Unknown",
        // which the existing code path already turns into a Warning — no
        // separate "forced alert" logic needed.
        var affected = notification.Window.TargetGroup is not null
            ? _servers.Where(s => s.Group.Equals(notification.Window.TargetGroup,
                StringComparison.OrdinalIgnoreCase))
            : _servers.Where(s => s.IP == notification.Window.ServerIp);

        foreach (var s in affected)
        {
            // Same per-server lock PingSingleServerAsync holds around its own
            // GetOrAdd+TryUpdate CAS on this exact dictionary — without it,
            // this raw write can land mid-CAS and silently drop or clobber
            // the transition log for a ping cycle already in flight for s.IP.
            var serverLock = GetServerLock(s.IP);
            await serverLock.WaitAsync(ct).ConfigureAwait(false);
            try { _previousStatus[s.IP] = PingStatus.Unknown; }
            finally { serverLock.Release(); }
        }
    }

    // ── BackgroundService ────────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Settings validation — guard against a malformed appsettings.json
        var offlineInterval = Math.Max(
            _settings.OfflinePingIntervalSeconds,
            MinOfflineIntervalSec);

        logger.LogInformation(
            "PingMonitorService started. {Count} servers, main: {Main}s, recovery: {Recovery}s.",
            _servers.Count, _settings.PingIntervalSeconds, offlineInterval);

        await mediator.Publish(AppLogEntryOccurred.Info(LogSource,
            $"Ping monitor started — {_servers.Count} server(s), " +
            $"main cycle: {_settings.PingIntervalSeconds}s, " +
            $"recovery cycle: {offlineInterval}s."), stoppingToken);

        await PublishInitialCheckingStateAsync(stoppingToken);

        // LinkedCts lets one loop cancel the other if it crashes.
        // Without this, if MainLoop crashes with an exception, RecoveryLoop
        // would keep running indefinitely, and vice versa.
        using var linkedCts = CancellationTokenSource
            .CreateLinkedTokenSource(stoppingToken);

        try
        {
            await Task.WhenAll(
                RunLoopGuardedAsync(RunMainLoopAsync(linkedCts.Token),     linkedCts),
                RunLoopGuardedAsync(RunRecoveryLoopAsync(offlineInterval,
                    linkedCts.Token),                  linkedCts)
            ).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "PingMonitorService: critical loop error.");
        }

        logger.LogInformation("PingMonitorService stopped.");

        // Audit Zone 1 — finding from live testing (2026-08-22): this line
        // sits AFTER the try/catch above (not inside it), so it wasn't
        // protected by anything. During an actual host shutdown (not just
        // stoppingToken cancellation, but also because the DI container has
        // already started disposing) this call can throw an
        // ObjectDisposedException ("IServiceProvider") rather than an
        // OperationCanceledException — confirmed by a live run. This is the
        // last line of the method, so any unhandled exception here would
        // likewise bubble out of ExecuteAsync.
        try
        {
            await mediator.Publish(AppLogEntryOccurred.Warning(LogSource, "Ping monitor stopped."), CancellationToken.None);
        }
        catch { /* worst case — the host is already disposing resources, the ILogger call above already recorded what matters */ }
    }

    // ── Main loop (all servers, every N seconds) ──────────────────────────

    private async Task RunMainLoopAsync(CancellationToken ct)
    {
        bool firstRun = true;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // First iteration — ping immediately, no delay.
                // Subsequent iterations — wait PingIntervalSeconds.
                if (firstRun)
                    firstRun = false;
                else
                    await Task.Delay(
                        TimeSpan.FromSeconds(_settings.PingIntervalSeconds),
                        ct).ConfigureAwait(false);

                if (ct.IsCancellationRequested) break;

                await PingServersAsync(_servers, _mainThrottle, ct)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal completion on StopAsync — ignore.
        }
    }

    // ── Recovery loop (Offline servers only, every M seconds) ──────────────

    private async Task RunRecoveryLoopAsync(int intervalSec, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(intervalSec),
                    ct).ConfigureAwait(false);

                if (ct.IsCancellationRequested) break;

                var offlineServers = _servers
                    .Where(s => _previousStatus.TryGetValue(s.IP, out var st)
                                && st == PingStatus.Offline)
                    .ToList();

                if (offlineServers.Count == 0) continue;

                logger.LogDebug(
                    "Recovery loop: pinging {Count} offline server(s).",
                    offlineServers.Count);

                await PingServersAsync(offlineServers, _recoveryThrottle, ct)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }

    // ── Loop guard ──────────────────────────────────────────────────────

    /// <summary>
    /// Wraps a loop: if the loop throws an unexpected exception —
    /// cancels linkedCts to stop the parallel loop, then rethrows the
    /// exception so Task.WhenAll sees it.
    /// OperationCanceledException — normal completion, ignored.
    /// </summary>
    private static async Task RunLoopGuardedAsync(
        Task                       loop,
        CancellationTokenSource    linkedCts)
    {
        try
        {
            await loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            // One loop failing → stop the other
            linkedCts.Cancel();
            throw;
        }
    }

    // ── Shared pinging logic ─────────────────────────────────────────────────

    /// <summary>
    /// Pings a list of servers in parallel through the given throttle,
    /// collects the results and publishes a single PingBatchResultOccurred.
    /// Used by both the main loop and the recovery loop — the only
    /// difference is the server list and the throttle.
    /// </summary>
    private async Task PingServersAsync(
        IEnumerable<ServerEntry> servers,
        SemaphoreSlim            throttle,
        CancellationToken        ct)
    {
        // Local bag — not a class field.
        // Each call to PingServersAsync has its own isolated bag,
        // so the main and recovery loops can't overwrite each other.
        var bag = new ConcurrentBag<PingResult>();

        var tasks = servers.Select(s => PingSingleServerAsync(s, throttle, bag, ct));
        await Task.WhenAll(tasks).ConfigureAwait(false);

        if (ct.IsCancellationRequested) return;

        // We'd publish even if the bag is empty (all OperationCanceled) —
        // the check above covers that.
        var results = bag.ToArray();
        if (results.Length == 0) return;

        await mediator.Publish(new PingBatchResultOccurred(
            new PingBatchPayload(
                Results:          results,
                CycleCompletedAt: DateTimeOffset.Now)), ct);
    }

    // ── Pinging a single server ───────────────────────────────────────────────

    private async Task PingSingleServerAsync(
        ServerEntry       server,
        SemaphoreSlim     throttle,
        ConcurrentBag<PingResult> bag,
        CancellationToken ct)
    {
        var acquired = false;
        var serverLock = GetServerLock(server.IP);
        var serverLockAcquired = false;
        try
        {
            await throttle.WaitAsync(ct).ConfigureAwait(false);
            acquired = true;  // slot acquired — Release() is now safe

            // Serialize just for this IP — if this server is already being
            // pinged by another call (main loop / recovery loop / /ping),
            // wait for it to finish before reading/writing
            // _previousStatus[ip] and sending transition logs.
            await serverLock.WaitAsync(ct).ConfigureAwait(false);
            serverLockAcquired = true;

            PingStatus status;
            long?      latencyMs = null;

            try
            {
                using var ping  = new Ping();
                var reply = await ping
                    .SendPingAsync(server.IP, PingTimeoutMs)
                    .WaitAsync(ct)
                    .ConfigureAwait(false);

                if (reply.Status == IPStatus.Success)
                {
                    status    = PingStatus.Online;
                    latencyMs = reply.RoundtripTime;
                }
                else
                {
                    status = PingStatus.Offline;
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                status = PingStatus.Offline;
                logger.LogWarning(ex,
                    "Ping to {Name} ({IP}) threw an exception.",
                    server.Name, server.IP);
            }

            var prev = _previousStatus.GetOrAdd(server.IP, PingStatus.Unknown);

            if (prev != status)
            {
                if (_previousStatus.TryUpdate(server.IP, status, prev))
                {
                    if (status == PingStatus.Online && prev == PingStatus.Offline)
                    {
                        await mediator.Publish(AppLogEntryOccurred.Success(LogSource,
                            $"{server.Name} ({server.IP}) is back ONLINE. " +
                            $"Latency: {latencyMs} ms."), ct);
                    }
                    else if (status == PingStatus.Offline)
                    {
                        bool underMaintenance = maintenance.IsUnderMaintenance(server.IP, server.Group);

                        if (!underMaintenance)
                        {
                            if (prev is PingStatus.Unknown or PingStatus.Checking)
                                await mediator.Publish(AppLogEntryOccurred.Warning(LogSource,
                                    $"{server.Name} ({server.IP}) is unreachable at startup."), ct);
                            else
                                await mediator.Publish(AppLogEntryOccurred.Error(LogSource,
                                    $"{server.Name} ({server.IP}) went OFFLINE."), ct);
                        }
                        // Under maintenance — no Warning/Error, but the status
                        // is still updated (PingResult below); the UI shows
                        // Offline + a 🔧 badge instead of an alert.
                    }
                    // Checking/Unknown → Online: silent, no log — avoid startup spam.
                }
            }

            bag.Add(new PingResult(
                server.Name, server.IP, server.Group,
                status, latencyMs, DateTimeOffset.Now));
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (serverLockAcquired) serverLock.Release();
            if (acquired) throttle.Release();
        }
    }

    // ── Initial state ─────────────────────────────────────────────────────────

    private async Task PublishInitialCheckingStateAsync(CancellationToken ct)
    {
        var initialResults = new List<PingResult>(_servers.Count);

        foreach (var server in _servers)
        {
            _previousStatus[server.IP] = PingStatus.Unknown;
            initialResults.Add(new PingResult(
                server.Name, server.IP, server.Group,
                PingStatus.Checking, null, DateTimeOffset.Now));
        }

        await mediator.Publish(new PingBatchResultOccurred(new PingBatchPayload(
            Results:          initialResults,
            CycleCompletedAt: DateTimeOffset.Now)), ct);
    }

    // ── Public API for TelegramBotService (Phase 5)

    /// <summary>
    /// A live snapshot of the current status of all servers right now.
    /// ConcurrentDictionary is already the single source of truth
    /// (_previousStatus), so this is a thin read-only method with no
    /// extra synchronization. Lets the bot answer correctly even in the
    /// first seconds after startup, without relying solely on
    /// PingBatchResultOccurred (which might not have arrived yet).
    /// </summary>
    public IReadOnlyDictionary<string, PingStatus> GetSnapshot()
        => _previousStatus.ToDictionary(kv => kv.Key, kv => kv.Value);

    /// <summary>
    /// Pings ALL servers right now, outside the regular cycle (REST GET
    /// /api/ping when opening Overview/Ping + the bot's /ping command — an
    /// on-demand "live" request). Reuses the same PingSingleServerAsync —
    /// meaning it:
    ///  - updates _previousStatus (the same state the UI sees);
    ///  - sends the same Warning/Error/Success logs on status changes;
    ///  - sends PingBatchResultOccurred — the Ping Dashboard UI updates too.
    /// Shares the throttle with the main loop (_mainThrottle) — no separate
    /// "parallel" load on the network beyond what's already scheduled.
    /// Also (audit fix 2026-08-22): _onDemandThrottle caps the FREQUENCY of
    /// the calls themselves to PingIntervalSeconds — a repeated REST/Telegram
    /// request within the window returns the just-obtained result instead of
    /// triggering a new real ping sweep.
    /// </summary>
    public Task<IReadOnlyList<PingResult>> PingAllNowAsync(CancellationToken ct) =>
        _onDemandThrottle.GetOrRunAsync(PingAllNowInternalAsync, ct);

    private async Task<IReadOnlyList<PingResult>> PingAllNowInternalAsync(CancellationToken ct)
    {
        var bag = new ConcurrentBag<PingResult>();

        var tasks = _servers.Select(s => PingSingleServerAsync(s, _mainThrottle, bag, ct));
        await Task.WhenAll(tasks).ConfigureAwait(false);

        // Audit fix (2026-08-22, on-demand throttling): PingSingleServerAsync
        // silently swallows its own OperationCanceledException (so one
        // canceled server doesn't fail the whole Task.WhenAll) — so
        // cancellation of the outer ct (client disconnected mid-poll) would
        // otherwise go unnoticed, and an INCOMPLETE/empty bag would get
        // cached by _onDemandThrottle as a valid result for the entire
        // PingIntervalSeconds window for all subsequent calls.
        ct.ThrowIfCancellationRequested();

        var results = bag.ToArray();
        if (results.Length > 0)
        {
            await mediator.Publish(new PingBatchResultOccurred(new PingBatchPayload(
                Results:          results,
                CycleCompletedAt: DateTimeOffset.Now)), ct);
        }

        return results
            .OrderBy(r => r.Group)
            .ThenBy(r => r.Name)
            .ToList();
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public override void Dispose()
    {
        // Both SemaphoreSlim instances hold an internal WaitHandle — dispose both.
        _mainThrottle.Dispose();
        _recoveryThrottle.Dispose();
        foreach (var l in _perServerLocks.Values) l.Dispose();
        base.Dispose();
    }
}
