using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using AdminConsole.Domain.Abstractions;
using AdminConsole.Domain.Events;
using AdminConsole.Domain.Models;
using AdminConsole.Infrastructure.Configuration;
using AdminConsole.Infrastructure.Monitoring;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AdminConsole.Infrastructure.Remote;

/// <summary>
/// Polls terminal servers via "quser /server:HOSTNAME".
///
/// IMPORTANT: we use the domain name (ServerEntry.Name), NOT the IP.
/// quser /server:TSVR3 — works over Named Pipes / NetBIOS.
/// quser /server:192.168.x.x — doesn't work (RPC over TCP, usually blocked).
///
/// Authorization: the backend service runs under a dedicated domain
/// account (DOMAIN\svc_adminconsole) with rights on the target servers —
/// quser.exe runs in the process's own context via Kerberos, without
/// separate RDP credentials and without CredWrite/CredRead around every
/// call.
///
/// T4.8: BackgroundService, a tight loop — no Hangfire.
///
/// IRecipient&lt;MonitoringToggledMessage&gt;
/// (WeakReferenceMessenger.Default.Register in the constructor) →
/// INotificationHandler&lt;MonitoringToggledOccurred&gt;
/// (resolved via DI by MediatR).
/// </summary>
public sealed class RdpMonitorService(
    IMediator                    mediator,
    ILogger<RdpMonitorService>   logger,
    IOptions<MonitoringSettings> settings,
    IOptions<List<ServerEntry>>  servers,
    IServiceScopeFactory         scopeFactory)
    : BackgroundService,
        INotificationHandler<MonitoringToggledOccurred>
{
    private readonly MonitoringSettings         _settings = settings.Value;
    private readonly IReadOnlyList<ServerEntry> _terminalServers = servers.Value
        .Where(s => s.Group.Equals("Terminal Servers", StringComparison.OrdinalIgnoreCase))
        .ToList()
        .AsReadOnly();

    private const string LogSource = "RdpMonitor";
    private const int    TimeoutMs = 30_000;

    // Step 1 ("HTTP 0" audit): the REST snapshot (GetSnapshotNowAsync) is
    // called synchronously from a browser that's waiting for the HTTP
    // response — 30s/server (TimeoutMs) from the background loop is too
    // long here and risks hitting an external proxy/browser timeout first.
    // For the REST path we use an explicit, shorter ceiling.
    private const int    SnapshotTimeoutMs = 15_000;
    private CancellationTokenSource? _wakeUpCts;

    // Audit fix (2026-08-22): throttling for the on-demand REST snapshot —
    // the window matches the background loop (RdpPollIntervalSeconds).
    // Without this, every visit to the RDP Sessions page independently
    // triggered quser.exe against a terminal server, regardless of the
    // configured interval.
    private readonly OnDemandSnapshotThrottle<(IReadOnlyList<RdpSessionInfo> Sessions, int GlobalDailyPeak,
        string? LastLogoutUsername, string? LastLogoutServer, DateTimeOffset? LastLogoutAt)> _onDemandThrottle
        = new(TimeSpan.FromSeconds(settings.Value.RdpPollIntervalSeconds));

    // Cache of the previous toggle state (null = never checked yet).
    // Lets us log and send MonitoringToggledOccurred only on an ACTUAL
    // state change, not on every poll cycle (anti-spam, edge case #2).
    private bool? _monitoringWasEnabled;

    private readonly ConcurrentDictionary<string, Dictionary<int, RdpSessionInfo>>
        _previousSessions = new();
    private readonly ConcurrentDictionary<string, bool> _firstPollDone = new();

    /// <summary>
    /// Per-server lock — mirrors PingMonitorService._perServerLocks. The
    /// background loop's own timer-driven poll and an on-demand REST
    /// snapshot (GetSnapshotNowAsync, only throttled AGAINST OTHER
    /// on-demand calls via _onDemandThrottle, not against the background
    /// loop) can otherwise call quser.exe against the SAME server
    /// concurrently — both read/write _previousSessions[ip] and diff
    /// against it independently, which can duplicate a connect/disconnect
    /// AppLogEntry and let whichever quser call happens to finish LAST
    /// (not first) win, regardless of which one actually reflects the
    /// current state. Serializes exactly at "one server" granularity —
    /// different servers still poll fully in parallel with each other.
    /// </summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _perServerLocks = new();

    private SemaphoreSlim GetServerLock(string ip) =>
        _perServerLocks.GetOrAdd(ip, _ => new SemaphoreSlim(1, 1));

    // ── Global state for Overview
    private int             _globalDailyPeak;
    private DateTime        _peakResetDate;
    private string?         _lastLogoutUsername;
    private string?         _lastLogoutServer;
    private DateTimeOffset? _lastLogoutAt;
    private readonly object _stateLock = new();

    // ── Regex ────────────────────────────────────────────────────────────────
    // WS2008R2 / WS2012+ format:
    //  USERNAME         SESSIONNAME    ID  STATE   IDLE TIME  LOGON TIME
    //  oleynikz         rdp-tcp#0      14  Active          .  11.06.2026 10:24
    //  yakymenko        rdp-tcp#1      15  Active          .  11.06.2026 10:29
    //  disconnecteduser                 3  Disc         1:30  11.06.2026 08:00

    private static readonly Regex ActiveRegex = new(
        @"^(?<user>\S+)\s+(?<session>\S+)\s+(?<id>\d+)\s+Active\s+(?<idle>\S+)\s+(?<logon>.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase,
        matchTimeout: TimeSpan.FromMilliseconds(500));

    private static readonly Regex DiscRegex = new(
        @"^(?<user>\S+)\s+(?<id>\d+)\s+Disc\s+(?<idle>\S+)\s+(?<logon>.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase,
        matchTimeout: TimeSpan.FromMilliseconds(500));

    // ── INotificationHandler ────────────────────────────────────────────────

    /// <summary>
    /// Reacts to the RDP monitoring toggle in Settings.
    /// Does NOT trust the Enabled field on the notification — it's only a
    /// signal to "wake up and check the source of truth yourself" (Pull,
    /// edge case #2). Used to resume polling immediately after monitoring
    /// is enabled, instead of waiting up to RdpPollIntervalSeconds.
    /// </summary>
    public Task Handle(MonitoringToggledOccurred notification, CancellationToken ct)
    {
        if (notification.Service != MonitoredService.Rdp) return Task.CompletedTask;

        WakeUp();
        return Task.CompletedTask;
    }

    private void WakeUp()
    {
        var cts = Interlocked.Exchange(ref _wakeUpCts, null);
        if (cts is null) return;
        try { cts.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// Checks the current RdpMonitoringEnabled state (Pull from
    /// IAppSettingsRepository, the source of truth — the AppSettings row in
    /// the DB) and, only on an ACTUAL change relative to the previous
    /// check, logs an event and sends MonitoringToggledOccurred to sync the
    /// UI (edge cases #2 and #3). Called every time, before the credential
    /// logic (edge case #1).
    /// </summary>
    private async Task<bool> EvaluateMonitoringToggleAsync(CancellationToken ct)
    {
        // IServiceScopeFactory instead of injecting IAppSettingsRepository
        // directly (Scoped) — this service is Singleton, same pattern as
        // MaintenanceService.
        AppSettings current;
        using (var scope = scopeFactory.CreateScope())
            current = await scope.ServiceProvider.GetRequiredService<IAppSettingsRepository>().GetAsync(ct);

        bool enabled = current.RdpMonitoringEnabled;

        if (_monitoringWasEnabled == enabled)
            return enabled; // state unchanged — stay quiet, no log spam

        bool isColdStart = _monitoringWasEnabled is null;
        _monitoringWasEnabled = enabled;

        if (isColdStart)
        {
            // Audit fix (2026-08-22, "Peak today: 0"): _globalDailyPeak
            // only lives in memory — a service restart (deploy/reboot) used
            // to reset it to 0, even if a session had been active and
            // disconnected EARLIER the same day — nobody was online
            // between then and the next restart for the peak to be
            // recalculated. Restore it from the DB if the record is still
            // for today.
            lock (_stateLock)
            {
                var today = DateTime.Now.Date;
                if (current.RdpDailyPeakDate.Date == today)
                {
                    _globalDailyPeak = current.RdpDailyPeak;
                    _peakResetDate   = today;
                }
            }
        }

        if (!enabled)
        {
            await mediator.Publish(AppLogEntryOccurred.Info(LogSource,
                "RDP monitoring disabled in Settings."), ct);
            await mediator.Publish(new MonitoringToggledOccurred(MonitoredService.Rdp, false), ct);
        }
        else if (!isColdStart)
        {
            await mediator.Publish(AppLogEntryOccurred.Info(LogSource,
                "RDP monitoring enabled — resuming polling."), ct);
            await mediator.Publish(new MonitoringToggledOccurred(MonitoredService.Rdp, true), ct);
        }

        return enabled;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_terminalServers.Count == 0)
        {
            // Previously only an ILogger call (invisible in the UI Logs).
            // The same class of "silent death" that ZabbixPollerService had
            // (Step 2, #5): if no server in Servers has Group == "Terminal
            // Servers" (typo/mismatch during appsettings.json migration),
            // RDP monitoring silently never starts at all.
            logger.LogInformation("RdpMonitorService: no Terminal Servers — idle.");
            await mediator.Publish(AppLogEntryOccurred.Warning(LogSource,
                "RDP: no server in Servers has Group=\"Terminal Servers\" — monitoring not started. " +
                "Check appsettings.json."), stoppingToken);
            return;
        }

        foreach (var server in _terminalServers)
        {
            if (System.Net.IPAddress.TryParse(server.Name, out _))
            {
                logger.LogWarning(
                    "RdpMonitorService: server '{Name}' has an IP address instead of a domain name.",
                    server.Name);
                await mediator.Publish(AppLogEntryOccurred.Warning(LogSource,
                    $"Configuration: '{server.Name}' is an IP, not a name. " +
                    $"quser may not work. Fix Name in appsettings.json."), stoppingToken);
            }
        }

        // Audit Zone 1 (2026-08-22): the entire method body is now under a
        // single try/catch. Previously EvaluateMonitoringToggleAsync/
        // PollAllServersAsync ran BEFORE the try block (completely
        // unprotected), and the try only caught OperationCanceledException —
        // any transient DB exception (e.g. SQLITE_BUSY from
        // IAppSettingsRepository.GetAsync) used to bubble up unhandled and
        // take down the whole host (BackgroundServiceExceptionBehavior).
        try
        {
            bool rdpMonitoringEnabled = await EvaluateMonitoringToggleAsync(stoppingToken);

            if (rdpMonitoringEnabled)
            {
                await mediator.Publish(AppLogEntryOccurred.Info(LogSource,
                    $"RDP monitor started — {_terminalServers.Count} server(s). " +
                    $"Using domain names for quser."), stoppingToken);
            }

            await PollAllServersAsync(stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                using var delayCts = CancellationTokenSource
                    .CreateLinkedTokenSource(stoppingToken);

                Interlocked.Exchange(ref _wakeUpCts, delayCts);

                try
                {
                    await Task.Delay(
                        TimeSpan.FromSeconds(_settings.RdpPollIntervalSeconds),
                        delayCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                    when (!stoppingToken.IsCancellationRequested)
                {
                    // Woken up by MonitoringToggledOccurred (edge case #2) —
                    // an immediate out-of-band poll right after monitoring is enabled.
                    await mediator.Publish(AppLogEntryOccurred.Info(LogSource,
                        "RDP: wake-up signal received — running an out-of-band poll."), stoppingToken);
                }

                Interlocked.Exchange(ref _wakeUpCts, null);

                if (stoppingToken.IsCancellationRequested) break;

                await PollAllServersAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "RdpMonitorService: critical loop error — monitoring stopped, app continues running.");
            try
            {
                await mediator.Publish(AppLogEntryOccurred.Error(LogSource,
                    $"RDP monitor: critical error, monitoring stopped: {ex.GetType().Name}: {ex.Message}"), CancellationToken.None);
            }
            catch { /* even the emergency log failed — the ILogger call above already recorded what matters */ }
        }
        finally
        {
            _wakeUpCts = null;
        }
    }

    // ── Poll coordination ───────────────────────────────────────────────

    private async Task PollAllServersAsync(CancellationToken ct)
    {
        // Toggle check — the VERY FIRST line. If RDP monitoring is
        // disabled — no quser.exe call at all.
        if (!await EvaluateMonitoringToggleAsync(ct))
            return;

        var tasks = _terminalServers.Select(s => PollServerAsync(s, ct));
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    // ── Polling a single server ────────────────────────────────────────────

    private Task PollServerAsync(ServerEntry server, CancellationToken ct)
        => PollServerOnceAsync(server, ct);

    private async Task PollServerOnceAsync(ServerEntry server, CancellationToken ct)
    {
        string hostname = server.Name;
        var serverLock = GetServerLock(server.IP);
        var serverLockAcquired = false;

        try
        {
            await serverLock.WaitAsync(ct).ConfigureAwait(false);
            serverLockAcquired = true;

            // quser.exe runs in the process's own context (the service runs
            // as DOMAIN\svc_adminconsole via Kerberos) — no credential
            // registration is needed before the call.
            var (output, error, exitCode) = await RunQuserAsync(hostname, ct).ConfigureAwait(false);

            string allText = (output + error).ToLowerInvariant();

            if (allText.Contains("logon failure") || allText.Contains("1326") ||
                allText.Contains("неверн") || allText.Contains("невірн"))
            {
                await LogSessionChangesAsync(server, [], ct);
                _previousSessions[server.IP] = new Dictionary<int, RdpSessionInfo>();
                await mediator.Publish(AppLogEntryOccurred.Warning(LogSource,
                    $"{hostname}: service account authentication failure — check DOMAIN\\svc_adminconsole permissions on this server."), ct);
                await mediator.Publish(new RdpSessionsUpdatedOccurred(await CreatePayloadAsync(
                    server.Name, server.IP, [], "Service account authentication failure", ct)), ct);
                return;
            }

            if (allText.Contains("access is denied") || allText.Contains("access denied"))
            {
                await LogSessionChangesAsync(server, [], ct);
                _previousSessions[server.IP] = new Dictionary<int, RdpSessionInfo>();
                await mediator.Publish(AppLogEntryOccurred.Warning(LogSource,
                    $"{hostname}: Access Denied — check DOMAIN\\svc_adminconsole permissions on this server."), ct);
                await mediator.Publish(new RdpSessionsUpdatedOccurred(await CreatePayloadAsync(
                    server.Name, server.IP, [], "Access Denied — check the service account permissions", ct)), ct);
                return;
            }

            if (allText.Contains("rpc server is unavailable") || allText.Contains("1722") || allText.Contains("0x000006ba"))
            {
                _previousSessions[server.IP] = new Dictionary<int, RdpSessionInfo>();
                await mediator.Publish(AppLogEntryOccurred.Error(LogSource,
                    $"{hostname}: RPC unavailable. Make sure appsettings.json has a domain name (not an IP)."), ct);
                await mediator.Publish(new RdpSessionsUpdatedOccurred(await CreatePayloadAsync(
                    server.Name, server.IP, [], "RPC unavailable — check the server name", ct)), ct);
                return;
            }

            if (string.IsNullOrWhiteSpace(output) || exitCode == 1)
            {
                bool noUsers = allText.Contains("no user") || allText.Contains("нет пользователей") || string.IsNullOrWhiteSpace(output);

                await LogSessionChangesAsync(server, [], ct);
                _previousSessions[server.IP] = [];

                await mediator.Publish(new RdpSessionsUpdatedOccurred(await CreatePayloadAsync(
                    server.Name, server.IP, [], noUsers ? null : $"Empty response (exit {exitCode})", ct)), ct);
                return;
            }

            var sessions = ParseQuserOutput(output, server.Name, server.IP);
            await LogSessionChangesAsync(server, sessions, ct);

            var newSnapshot = new Dictionary<int, RdpSessionInfo>();
            foreach (var s in sessions)
            {
                if (int.TryParse(s.SessionId, out int id))
                    newSnapshot[id] = s;
            }
            _previousSessions[server.IP] = newSnapshot;

            await mediator.Publish(new RdpSessionsUpdatedOccurred(await CreatePayloadAsync(
                server.Name, server.IP, sessions, null, ct)), ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _previousSessions[server.IP] = new Dictionary<int, RdpSessionInfo>();
            logger.LogWarning(ex, "RdpMonitorService: error polling {Server}", hostname);
            await mediator.Publish(AppLogEntryOccurred.Error(LogSource, $"{hostname}: {ex.GetType().Name}: {ex.Message}"), ct);
            await mediator.Publish(new RdpSessionsUpdatedOccurred(await CreatePayloadAsync(
                server.Name, server.IP, [], ex.Message, ct)), ct);
        }
        finally
        {
            if (serverLockAcquired) serverLock.Release();
        }
    }

    // ── State Diffing ────────────────────────────────────────────────────────

    /// <summary>
    /// Compares the current session list against the previous snapshot and
    /// logs only the changes. On the first poll for a server — silently
    /// populates the dictionary without logging, so we don't spam
    /// "connected" for sessions that already existed when the app started.
    /// </summary>
    private async Task LogSessionChangesAsync(ServerEntry server, List<RdpSessionInfo> currentSessions, CancellationToken ct)
    {
        // Build the current snapshot with valid int SessionIds
        var currentSnapshot = new Dictionary<int, RdpSessionInfo>();
        foreach (var s in currentSessions)
        {
            if (int.TryParse(s.SessionId, out int id))
                currentSnapshot[id] = s;
        }

        bool isFirst = _firstPollDone.TryAdd(server.IP, true);
        if (isFirst) return;

        _previousSessions.TryGetValue(server.IP, out var previousSnapshot);
        previousSnapshot ??= [];

        foreach (var (id, current) in currentSnapshot)
        {
            if (!previousSnapshot.TryGetValue(id, out var previous))
            {
                await mediator.Publish(AppLogEntryOccurred.Info(LogSource,
                    $"{current.Username} → connected to {server.Name} " +
                    $"(session #{id}, logon: {current.LogonTime})"), ct);
                continue;
            }

            if (previous.State == RdpSessionState.Active &&
                current.State  == RdpSessionState.Disconnected)
            {
                await mediator.Publish(AppLogEntryOccurred.Info(LogSource,
                    $"{current.Username} → session went idle on {server.Name} " +
                    $"(Active → Disconnected, logon: {current.LogonTime})"), ct);

                // Audit fix (2026-08-22, "Last logout: —"): previously
                // _lastLogoutUsername was only updated when a session fully
                // disappeared from quser's output (a real logoff). In
                // practice, users much more often simply close the RDP
                // client WITHOUT logging out — the session stays on the
                // server in the Disconnected state (as seen in the SESSIONS
                // table), and "Last logout"/the Overview card never learned
                // about it. Active → Disconnected is exactly what a regular
                // user means by "logout".
                lock (_stateLock)
                {
                    _lastLogoutUsername = current.Username;
                    _lastLogoutServer   = server.Name;
                    _lastLogoutAt       = DateTimeOffset.Now;
                }
            }
            else if (previous.State == RdpSessionState.Disconnected &&
                     current.State  == RdpSessionState.Active)
            {
                await mediator.Publish(AppLogEntryOccurred.Info(LogSource,
                    $"{current.Username} → session resumed on {server.Name} " +
                    $"(Disconnected → Active, logon: {current.LogonTime})"), ct);
            }
        }

        foreach (var (id, previous) in previousSnapshot)
        {
            if (!currentSnapshot.ContainsKey(id))
            {
                string duration = TryCalculateDuration(previous.LogonTime);
                string durationPart = duration.Length > 0 ? $", duration: {duration}" : string.Empty;

                await mediator.Publish(AppLogEntryOccurred.Info(LogSource,
                    $"{previous.Username} → disconnected from {server.Name} " +
                    $"(session #{id}, was connected since {previous.LogonTime}{durationPart})"), ct);

                lock (_stateLock)
                {
                    _lastLogoutUsername = previous.Username;
                    _lastLogoutServer   = server.Name;
                    _lastLogoutAt       = DateTimeOffset.Now;
                }
            }
        }
    }

    /// <summary>
    /// Attempts to calculate session duration from quser's LogonTime string.
    /// quser returns the format "dd.MM.yyyy HH:mm" or "MM/dd/yyyy h:mm AM/PM".
    /// Returns an empty string if parsing fails.
    /// </summary>
    private static string TryCalculateDuration(string logonTime)
    {
        if (string.IsNullOrWhiteSpace(logonTime)) return string.Empty;

        string[] formats =
        [
            "dd.MM.yyyy HH:mm",
            "d.MM.yyyy H:mm",
            "MM/dd/yyyy h:mm tt",
            "M/d/yyyy h:mm tt",
            "dd.MM.yyyy H:mm",
        ];

        if (!DateTime.TryParseExact(logonTime.Trim(), formats,
            System.Globalization.CultureInfo.CurrentCulture,
            System.Globalization.DateTimeStyles.None,
            out var logon))
        {
            return string.Empty;
        }

        var duration = DateTime.Now - logon;

        if (duration.TotalDays >= 1)
            return $"{(int)duration.TotalDays}d {duration.Hours}h {duration.Minutes}m";
        if (duration.TotalHours >= 1)
            return $"{(int)duration.TotalHours}h {duration.Minutes}m";
        return $"{(int)duration.TotalMinutes}m";
    }

    // ── quser ────────────────────────────────────────────────────────────────

    private static async Task<(string Output, string Error, int ExitCode)> RunQuserAsync(
        string hostname, CancellationToken ct)
    {
        Encoding consoleEncoding;
        try
        {
            int oemPage = System.Globalization.CultureInfo
                .CurrentCulture.TextInfo.OEMCodePage;
            consoleEncoding = Encoding.GetEncoding(oemPage);
        }
        catch
        {
            consoleEncoding = new UTF8Encoding(false);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeoutMs);

        using var p = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName               = "quser.exe",
                Arguments              = $"/server:{hostname}",
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true,
                StandardOutputEncoding = consoleEncoding,
                StandardErrorEncoding  = consoleEncoding
            },
            EnableRaisingEvents = true
        };

        p.Start();

        var outputTask = p.StandardOutput.ReadToEndAsync(cts.Token);
        var errorTask  = p.StandardError.ReadToEndAsync(cts.Token);

        try
        {
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            string output = await outputTask.ConfigureAwait(false);
            string error  = await errorTask.ConfigureAwait(false);
            return (output, error, p.ExitCode);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            string partial = "";
            try { partial = await outputTask.ConfigureAwait(false); } catch { }
            return (partial, "Timeout: server did not respond within 30 seconds", -1);
        }
    }

    // ── quser output parser ──────────────────────────────────────────────────

    private List<RdpSessionInfo> ParseQuserOutput(
        string raw, string serverName, string serverIp)
    {
        var results = new List<RdpSessionInfo>();
        if (string.IsNullOrWhiteSpace(raw)) return results;

        foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string normalized = line.Trim().TrimStart('>').Trim();

            if (string.IsNullOrWhiteSpace(normalized))                                    continue;
            if (normalized.StartsWith("USERNAME",    StringComparison.OrdinalIgnoreCase)) continue;
            if (normalized.StartsWith("SESSIONNAME", StringComparison.OrdinalIgnoreCase)) continue;

            try
            {
                var match = ActiveRegex.Match(normalized);
                if (match.Success)
                {
                    results.Add(new RdpSessionInfo(
                        Username:    match.Groups["user"].Value.Trim(),
                        SessionName: match.Groups["session"].Value.Trim(),
                        SessionId:   match.Groups["id"].Value.Trim(),
                        State:       RdpSessionState.Active,
                        IdleTime:    match.Groups["idle"].Value.Trim(),
                        LogonTime:   match.Groups["logon"].Value.Trim(),
                        ServerName:  serverName,
                        ServerIp:    serverIp));
                    continue;
                }

                match = DiscRegex.Match(normalized);
                if (match.Success)
                {
                    results.Add(new RdpSessionInfo(
                        Username:    match.Groups["user"].Value.Trim(),
                        SessionName: "—",
                        SessionId:   match.Groups["id"].Value.Trim(),
                        State:       RdpSessionState.Disconnected,
                        IdleTime:    match.Groups["idle"].Value.Trim(),
                        LogonTime:   match.Groups["logon"].Value.Trim(),
                        ServerName:  serverName,
                        ServerIp:    serverIp));
                }
            }
            catch (RegexMatchTimeoutException)
            {
                logger.LogWarning(
                    "RdpMonitorService: regex timeout on line from {Server}: {Line}",
                    serverName,
                    normalized.Length > 120 ? normalized[..120] + "…" : normalized);
            }
        }

        return results;
    }

    // ── Public API for TelegramBotService (Phase 5)

    /// <summary>
    /// A live snapshot of current RDP sessions for each terminal server
    /// (keyed by IP). _previousSessions is already a ConcurrentDictionary —
    /// safe to read from any thread. Values are copied (.ToList()) so the
    /// caller doesn't hold a reference to the internal Dictionary, which
    /// the poller can update concurrently.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<RdpSessionInfo>> GetSnapshot()
        => _previousSessions.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<RdpSessionInfo>)kv.Value.Values.ToList());

    /// <summary>
    /// Live poll of all terminal servers (quser) RIGHT NOW + an aggregated
    /// snapshot — for the initial REST load of the RDP Sessions page
    /// (Phase 10, audit Step 11.2 — previously the page only had a SignalR
    /// stream). The same PollAllServersAsync call as the background loop —
    /// publishes the same RdpSessionsUpdatedOccurred events (the client
    /// that triggered the request will see the data both in the response
    /// and via SignalR), and equally respects RdpMonitoringEnabled.
    ///
    /// Audit fix (2026-08-22): _onDemandThrottle caps the FREQUENCY of
    /// calls to RdpPollIntervalSeconds — a repeated request within the
    /// window (F5, several open tabs) returns the just-obtained snapshot
    /// instead of a new quser.exe call against the server.
    /// </summary>
    public Task<(IReadOnlyList<RdpSessionInfo> Sessions, int GlobalDailyPeak,
        string? LastLogoutUsername, string? LastLogoutServer, DateTimeOffset? LastLogoutAt)>
        GetSnapshotNowAsync(CancellationToken ct) =>
        _onDemandThrottle.GetOrRunAsync(GetSnapshotNowInternalAsync, ct);

    private async Task<(IReadOnlyList<RdpSessionInfo> Sessions, int GlobalDailyPeak,
        string? LastLogoutUsername, string? LastLogoutServer, DateTimeOffset? LastLogoutAt)>
        GetSnapshotNowInternalAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        await mediator.Publish(AppLogEntryOccurred.Info(LogSource,
            $"RDP: REST snapshot request received — polling {_terminalServers.Count} server(s) " +
            $"(limit {SnapshotTimeoutMs / 1000}s)."), ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(SnapshotTimeoutMs);

        await PollAllServersAsync(timeoutCts.Token).ConfigureAwait(false);
        sw.Stop();

        if (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            await mediator.Publish(AppLogEntryOccurred.Warning(LogSource,
                $"RDP: REST snapshot exceeded the {SnapshotTimeoutMs / 1000}s limit ({sw.ElapsedMilliseconds}ms) — " +
                "some servers may not have responded in time; returning the last known data."), ct);
        }
        else
        {
            await mediator.Publish(AppLogEntryOccurred.Info(LogSource,
                $"RDP: REST snapshot completed in {sw.ElapsedMilliseconds}ms."), ct);
        }

        var sessions = _previousSessions.Values.SelectMany(d => d.Values).ToList();
        lock (_stateLock)
        {
            return (sessions, _globalDailyPeak, _lastLogoutUsername, _lastLogoutServer, _lastLogoutAt);
        }
    }

    // Audit Zone 1, Finding #8 (2026-08-22): previously, computing the new
    // peak (under _stateLock) and persisting it (await PersistDailyPeakAsync,
    // OUTSIDE _stateLock — a lock can't wrap an await) were two separate
    // steps. If several servers are polled in parallel (Task.WhenAll in
    // PollAllServersAsync) and their async DB writes complete in a
    // different order than they were computed in, a newer (higher) peak
    // could theoretically be overwritten by an older value written later.
    // _peakGate serializes the ENTIRE "compute + save" chain as one atomic
    // unit — no two calls can interleave out of order anymore.
    private readonly SemaphoreSlim _peakGate = new(1, 1);

    /// <summary>
    /// Computes the global peak and builds the Payload. This guarantees the
    /// client gets consistent historical data, and "Peak today" survives a
    /// service restart within the same day (see
    /// EvaluateMonitoringToggleAsync).
    /// </summary>
    private async Task<RdpSessionsPayload> CreatePayloadAsync(
        string serverName, string serverIp, IReadOnlyList<RdpSessionInfo> sessions, string? errorMessage,
        CancellationToken ct)
    {
        int globalPeak;

        await _peakGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            bool peakChanged;
            int  peakToPersist;
            DateTime dateToPersist;

            lock (_stateLock)
            {
                var today = DateTime.Now.Date;
                if (_peakResetDate != today)
                {
                    _peakResetDate = today;
                    _globalDailyPeak = 0;
                }

                int currentTotalActive = _previousSessions.Values
                    .SelectMany(dict => dict.Values)
                    .Count(s => s.State == RdpSessionState.Active);

                peakChanged = currentTotalActive > _globalDailyPeak;
                if (peakChanged)
                    _globalDailyPeak = currentTotalActive;

                peakToPersist = _globalDailyPeak;
                dateToPersist = _peakResetDate;
            }

            if (peakChanged)
                await PersistDailyPeakAsync(peakToPersist, dateToPersist, ct).ConfigureAwait(false);

            globalPeak = peakToPersist;
        }
        finally
        {
            _peakGate.Release();
        }

        lock (_stateLock)
        {
            return new RdpSessionsPayload(
                serverName, serverIp, sessions, errorMessage,
                globalPeak, _lastLogoutUsername, _lastLogoutServer, _lastLogoutAt);
        }
    }

    private async Task PersistDailyPeakAsync(int peak, DateTime date, CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IAppSettingsRepository>();
            // Audit Zone 2 (2026-08-22): a targeted update of just two
            // fields — not GetAsync+SaveAsync of the full object, so a
            // concurrent write (e.g. a user saving the monitoring toggles
            // in Settings at that exact moment) can't overwrite the peak
            // with its own stale copy.
            await repo.UpdateRdpDailyPeakAsync(peak, date, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "RdpMonitorService: failed to save RdpDailyPeak to AppSettings.");
        }
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public override void Dispose()
    {
        foreach (var l in _perServerLocks.Values) l.Dispose();
        base.Dispose();
    }
}
