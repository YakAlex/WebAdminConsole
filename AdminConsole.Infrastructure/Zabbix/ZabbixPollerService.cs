using AdminConsole.Domain.Abstractions;
using AdminConsole.Domain.Events;
using AdminConsole.Infrastructure.Configuration;
using AdminConsole.Infrastructure.Monitoring;
using AdminConsole.Infrastructure.Security;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AdminConsole.Infrastructure.Zabbix;

/// <summary>
/// T4.11: BackgroundService, tight loop (60s) — no Hangfire.
///
/// ICredentialPrompt (T4.14, removed) — the headless service doesn't show
/// a token-entry dialog; if credentials are missing, it just waits for
/// CredentialsChangedOccurred (future Settings API, Phase 5).
///
/// IRecipient&lt;CredentialsChangedMessage&gt;/&lt;MonitoringToggledMessage&gt; →
/// INotificationHandler&lt;...&gt; (MediatR DI resolution).
/// </summary>
public sealed class ZabbixPollerService(
    IMediator                     mediator,
    ILogger<ZabbixPollerService>  logger,
    IOptions<MonitoringSettings>  settings,
    ZabbixApiClient                client,
    CredentialStore                credentials,
    IServiceScopeFactory           scopeFactory)
    : BackgroundService,
        INotificationHandler<CredentialsChangedOccurred>,
        INotificationHandler<MonitoringToggledOccurred>
{
    private readonly MonitoringSettings _settings = settings.Value;

    private static readonly int[] WatchedSeverities = [4, 5];
    private const string LogSource = "ZabbixPoller";
    private CancellationTokenSource? _wakeUpCts;
    private bool _hasLoggedStart;

    // Cache of the previous toggle state (null = never checked yet).
    private bool? _monitoringWasEnabled;

    // Audit fix (2026-08-22): throttling for the on-demand REST snapshot — same
    // window as the background loop (ZabbixPollIntervalSeconds).
    private readonly OnDemandSnapshotThrottle<ZabbixProblemsPayload> _onDemandThrottle =
        new(TimeSpan.FromSeconds(settings.Value.ZabbixPollIntervalSeconds));

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await credentials.LoadZabbixFromStoreAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ZabbixPollerService: failed to load credentials.");
        }

        await base.StartAsync(cancellationToken);
    }

    public Task Handle(CredentialsChangedOccurred notification, CancellationToken ct)
    {
        if (notification.Target != CredentialTarget.Zabbix) return Task.CompletedTask;
        if (notification.Action != CredentialAction.Saved) return Task.CompletedTask;

        WakeUp();
        return Task.CompletedTask;
    }

    public Task Handle(MonitoringToggledOccurred notification, CancellationToken ct)
    {
        if (notification.Service != MonitoredService.Zabbix) return Task.CompletedTask;

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
    /// Checks the current ZabbixMonitoringEnabled state (pulled from
    /// IAppSettingsRepository) and, only on an ACTUAL change, logs the event
    /// and publishes MonitoringToggledOccurred to sync the UI.
    /// Called every time, before the credential logic (edge case #1).
    /// </summary>
    private async Task<bool> EvaluateMonitoringToggleAsync(CancellationToken ct)
    {
        // IServiceScopeFactory instead of directly injecting IAppSettingsRepository
        // (Scoped) — this service is a Singleton, same pattern as MaintenanceService.
        AdminConsole.Domain.Models.AppSettings current;
        using (var scope = scopeFactory.CreateScope())
            current = await scope.ServiceProvider.GetRequiredService<IAppSettingsRepository>().GetAsync(ct);

        bool enabled = current.ZabbixMonitoringEnabled;

        if (_monitoringWasEnabled == enabled)
            return enabled; // state unchanged — stay quiet, don't spam the logs

        bool isColdStart = _monitoringWasEnabled is null;
        _monitoringWasEnabled = enabled;

        if (!enabled)
        {
            await mediator.Publish(AppLogEntryOccurred.Info(LogSource,
                "Zabbix monitoring disabled in Settings."), ct);
            await mediator.Publish(new MonitoringToggledOccurred(MonitoredService.Zabbix, false), ct);
        }
        else if (!isColdStart)
        {
            await mediator.Publish(AppLogEntryOccurred.Info(LogSource,
                "Zabbix monitoring enabled — resuming polling."), ct);
            await mediator.Publish(new MonitoringToggledOccurred(MonitoredService.Zabbix, true), ct);
        }

        return enabled;
    }

    // ── BackgroundService ────────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_settings.ZabbixUrl))
        {
            // Previously this was only logged via ILogger — invisible in the UI Logs
            // (UX backlog #5: "zero information in the logs"). Publishing here too,
            // because this is exactly the case where the admin would see nothing at all.
            logger.LogInformation("ZabbixPollerService: ZabbixUrl is not configured — idle.");
            await mediator.Publish(AppLogEntryOccurred.Warning(LogSource,
                "Zabbix: Monitoring:ZabbixUrl is not configured in appsettings.json — poller not started."), stoppingToken);
            return;
        }

        // Audit Zone 1 (2026-08-22): the entire method body is now under a single
        // try/catch. Previously EvaluateMonitoringToggleAsync/AuthenticateAsync/PollAsync
        // ran BEFORE the try block (with no protection at all), and the try itself only
        // caught OperationCanceledException — any transient DB exception (e.g.
        // SQLITE_BUSY from IAppSettingsRepository.GetAsync) escaped uncaught and took
        // down the whole host (BackgroundServiceExceptionBehavior).
        try
        {
            bool zabbixMonitoringEnabled = await EvaluateMonitoringToggleAsync(stoppingToken);

            if (zabbixMonitoringEnabled && !credentials.HasZabbixCredentials)
            {
                await mediator.Publish(AppLogEntryOccurred.Info(LogSource,
                    "Zabbix: credentials missing — waiting for them to be saved via the Settings API."), stoppingToken);
            }

            // Credentials are present and monitoring is enabled — start up fully
            if (zabbixMonitoringEnabled && credentials.HasZabbixCredentials)
            {
                await LogStartedAsync(stoppingToken);
                _hasLoggedStart = true;

                await PollAsync(stoppingToken).ConfigureAwait(false);
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                using var delayCts = CancellationTokenSource
                    .CreateLinkedTokenSource(stoppingToken);

                Interlocked.Exchange(ref _wakeUpCts, delayCts);

                try
                {
                    await Task.Delay(
                        TimeSpan.FromSeconds(_settings.ZabbixPollIntervalSeconds),
                        delayCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                    when (!stoppingToken.IsCancellationRequested)
                {
                    await mediator.Publish(AppLogEntryOccurred.Info(LogSource,
                        "Zabbix: wake-up signal received — running an out-of-cycle poll."), stoppingToken);
                }

                Interlocked.Exchange(ref _wakeUpCts, null);

                if (stoppingToken.IsCancellationRequested) break;

                // EDGE CASE #1: toggle check — the VERY FIRST thing on every iteration,
                // BEFORE checking HasZabbixCredentials.
                if (!await EvaluateMonitoringToggleAsync(stoppingToken))
                    continue;

                if (!credentials.HasZabbixCredentials)
                {
                    await mediator.Publish(AppLogEntryOccurred.Info(LogSource,
                        "Zabbix: credentials missing — poll skipped."), stoppingToken);
                    continue;
                }

                // First successful wake-up after starting without credentials — log
                // the startup ONCE. UX backlog #5: previously nothing appeared in
                // the logs until the first successful/failed poll.
                if (!_hasLoggedStart)
                {
                    await LogStartedAsync(stoppingToken);
                    _hasLoggedStart = true;
                }

                await PollAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown on StopAsync — ignore.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ZabbixPollerService: fatal loop error — monitoring stopped, application continues running.");
            try
            {
                await mediator.Publish(AppLogEntryOccurred.Error(LogSource,
                    $"Zabbix poller: fatal error, monitoring stopped: {ex.GetType().Name}: {ex.Message}"), CancellationToken.None);
            }
            catch { /* even the fallback log failed — ILogger above already captured the essentials */ }
        }
        finally
        {
            _wakeUpCts = null;
        }
    }

    // ── Poll ─────────────────────────────────────────────────────────────────

    private const int MaxAuthRetries = 3;
    private int _consecutiveAuthFailures;

    // null = no poll has happened yet. UX backlog #5: successful cycles used to
    // write nothing to AppLogEntries at all — silence looked identical whether
    // "everything's fine, just 0 High/Disaster problems" or "the integration is dead".
    // Log Success only on the TRANSITION into a working state (not every cycle — 180s spam).
    private bool? _lastPollSucceeded;

    private async Task PollAsync(CancellationToken ct)
    {
        var tokenUsedForRequest = credentials.GetZabbixToken();
        string auth = tokenUsedForRequest;

        try
        {
            var problems = await client.GetActiveProblemsAsync(
                _settings.ZabbixUrl, auth,
                WatchedSeverities, ct).ConfigureAwait(false);

            _consecutiveAuthFailures = 0;

            if (_lastPollSucceeded != true)
            {
                await mediator.Publish(AppLogEntryOccurred.Success(LogSource,
                    $"Zabbix: connection working, found {problems.Count} active problems (severity High/Disaster)."), ct);
            }
            _lastPollSucceeded = true;

            await mediator.Publish(new ZabbixProblemsUpdatedOccurred(new ZabbixProblemsPayload(
                Problems: problems,
                ErrorMessage: null,
                FetchedAt: DateTimeOffset.Now)), ct);

            return;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (ZabbixAuthException ex)
        {
            logger.LogWarning("ZabbixPollerService: auth rejected — {Msg}", ex.Message);
            var currentTokenInVault = credentials.GetZabbixToken();
            if (!string.IsNullOrWhiteSpace(currentTokenInVault)
                && currentTokenInVault != tokenUsedForRequest)
            {
                logger.LogInformation(
                    "Zabbix: token was updated during the request — ignoring the stale-token error.");
                _consecutiveAuthFailures = 0;
                return;
            }

            // Do NOT delete the token — the background service has no right to erase
            // credentials. Only the user can remove the token via Settings.
            _consecutiveAuthFailures++;
            _lastPollSucceeded = false;

            await mediator.Publish(new ZabbixProblemsUpdatedOccurred(new ZabbixProblemsPayload(
                Problems: [],
                ErrorMessage: $"Zabbix rejected the token: {ex.Message}",
                FetchedAt: DateTimeOffset.Now)), ct);

            if (_consecutiveAuthFailures >= MaxAuthRetries)
            {
                await mediator.Publish(AppLogEntryOccurred.Warning(LogSource,
                    $"Zabbix: {MaxAuthRetries} consecutive poll cycles with an invalid token. " +
                    $"Reason: {ex.Message} Update the token in Settings."), ct);
            }
            else
            {
                await mediator.Publish(AppLogEntryOccurred.Warning(LogSource,
                    $"Zabbix: token rejected (cycle {_consecutiveAuthFailures}/{MaxAuthRetries}). " +
                    $"Reason: {ex.Message} Update the token in Settings."), ct);
            }
            return;
        }
        catch (Exception ex)
        {
            _lastPollSucceeded = false;
            logger.LogWarning(ex, "ZabbixPollerService: poll failed.");
            await mediator.Publish(AppLogEntryOccurred.Error(LogSource,
                $"Zabbix poll failed: {ex.Message}"), ct);

            await mediator.Publish(new ZabbixProblemsUpdatedOccurred(new ZabbixProblemsPayload(
                Problems: null,
                ErrorMessage: $"Connection error: {ex.Message}",
                FetchedAt: DateTimeOffset.Now)), ct);

            return;
        }
    }

    // ── On-demand (REST) ─────────────────────────────────────────────────────

    /// <summary>
    /// Live poll of Zabbix RIGHT NOW, for the initial REST snapshot of the Zabbix
    /// Alerts page (T6.2/Step 11.1) — same principle already used by
    /// PingMonitorService.PingAllNowAsync. Deliberately does NOT touch
    /// _consecutiveAuthFailures/_lastPollSucceeded — that's the bookkeeping of the
    /// background poll loop, and a one-off REST request shouldn't affect its state.
    ///
    /// Audit fix (2026-08-22): _onDemandThrottle caps the call FREQUENCY to
    /// ZabbixPollIntervalSeconds — a repeat request within the window returns the
    /// just-fetched snapshot instead of firing a new live request against the
    /// Zabbix API.
    /// </summary>
    public Task<ZabbixProblemsPayload> GetActiveProblemsNowAsync(CancellationToken ct) =>
        _onDemandThrottle.GetOrRunAsync(GetActiveProblemsNowInternalAsync, ct);

    private async Task<ZabbixProblemsPayload> GetActiveProblemsNowInternalAsync(CancellationToken ct)
    {
        // Audit fix item 4: previously this method IGNORED ZabbixMonitoringEnabled
        // entirely — even after the toggle was switched off in Settings, every visit
        // to Overview/Zabbix Alerts still fired a live request against the real
        // Zabbix API. Same Pull pattern already used in RdpMonitorService.
        // PollAllServersAsync — check is the VERY FIRST line.
        if (!await EvaluateMonitoringToggleAsync(ct))
            return new ZabbixProblemsPayload(null, "Zabbix monitoring is disabled in Settings.", DateTimeOffset.Now);

        if (string.IsNullOrWhiteSpace(_settings.ZabbixUrl))
            return new ZabbixProblemsPayload(null, "Monitoring:ZabbixUrl is not configured.", DateTimeOffset.Now);

        if (!credentials.HasZabbixCredentials)
            return new ZabbixProblemsPayload(null, "Credentials are not saved (Settings → Zabbix Token).", DateTimeOffset.Now);

        string auth = credentials.GetZabbixToken();

        try
        {
            var problems = await client.GetActiveProblemsAsync(
                _settings.ZabbixUrl, auth, WatchedSeverities, ct).ConfigureAwait(false);
            return new ZabbixProblemsPayload(problems, null, DateTimeOffset.Now);
        }
        // Audit fix (2026-08-22, on-demand throttling): the catch (Exception) below
        // used to also catch OperationCanceledException — a cancelled request (client
        // disconnected) turned into a fake "Connection error" payload, which
        // _onDemandThrottle then cached for ZabbixPollIntervalSeconds for ALL
        // subsequent calls, including ones whose ct was never going to be cancelled.
        // Rethrow (without caching) when the cancellation genuinely comes from this
        // call's own ct; otherwise (a real HttpClient timeout/Zabbix error) — as
        // before, return an error message.
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ZabbixProblemsPayload(null, $"Connection error: {ex.Message}", DateTimeOffset.Now);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task LogStartedAsync(CancellationToken ct)
    {
        logger.LogInformation(
            "ZabbixPollerService started. Auth: API Token. Interval: {Interval}s.",
            _settings.ZabbixPollIntervalSeconds);

        await mediator.Publish(AppLogEntryOccurred.Info(LogSource,
            $"Zabbix poller started (API token auth). " +
            $"Polling every {_settings.ZabbixPollIntervalSeconds}s."), ct);
    }
}
