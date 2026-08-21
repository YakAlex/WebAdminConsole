using AdminConsole.Domain.Abstractions;
using AdminConsole.Domain.Events;
using AdminConsole.Infrastructure.Configuration;
using AdminConsole.Infrastructure.Security;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AdminConsole.Infrastructure.Zabbix;

/// <summary>
/// T4.11: BackgroundService, тісний цикл (60с) — без Hangfire.
///
/// ICredentialPrompt (T4.14, видалено) — headless-сервіс не показує
/// діалог вводу токена; якщо credentials відсутні, просто чекає на
/// CredentialsChangedOccurred (майбутній Settings API, Фаза 5).
///
/// IRecipient&lt;CredentialsChangedMessage&gt;/&lt;MonitoringToggledMessage&gt; →
/// INotificationHandler&lt;...&gt; (DI-резолв MediatR).
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
    private string? _sessionToken;
    private CancellationTokenSource? _wakeUpCts;
    private bool _hasLoggedStart;

    // Кеш попереднього стану toggle (null = ще не перевіряли жодного разу).
    private bool? _monitoringWasEnabled;

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await credentials.LoadZabbixFromStoreAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ZabbixPollerService: не вдалось завантажити credentials.");
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
    /// Перевіряє поточний стан ZabbixMonitoringEnabled (Pull з
    /// IAppSettingsRepository) і, лише при РЕАЛЬНІЙ зміні, логує подію
    /// та шле MonitoringToggledOccurred для синхронізації UI.
    /// Виклик — щоразу перед credential-логікою (edge-case #1).
    /// </summary>
    private async Task<bool> EvaluateMonitoringToggleAsync(CancellationToken ct)
    {
        // IServiceScopeFactory замість прямої ін'єкції IAppSettingsRepository
        // (Scoped) — цей сервіс Singleton, той самий патерн, що MaintenanceService.
        AdminConsole.Domain.Models.AppSettings current;
        using (var scope = scopeFactory.CreateScope())
            current = await scope.ServiceProvider.GetRequiredService<IAppSettingsRepository>().GetAsync(ct);

        bool enabled = current.ZabbixMonitoringEnabled;

        if (_monitoringWasEnabled == enabled)
            return enabled; // стан не змінився — тиша, без спаму логів

        bool isColdStart = _monitoringWasEnabled is null;
        _monitoringWasEnabled = enabled;

        if (!enabled)
        {
            await mediator.Publish(AppLogEntryOccurred.Info(LogSource,
                "Zabbix моніторинг вимкнено в Settings."), ct);
            await mediator.Publish(new MonitoringToggledOccurred(MonitoredService.Zabbix, false), ct);
        }
        else if (!isColdStart)
        {
            await mediator.Publish(AppLogEntryOccurred.Info(LogSource,
                "Zabbix моніторинг увімкнено — відновлюємо опитування."), ct);
            await mediator.Publish(new MonitoringToggledOccurred(MonitoredService.Zabbix, true), ct);
        }

        return enabled;
    }

    // ── BackgroundService ────────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_settings.ZabbixUrl))
        {
            // Раніше це логувалось лише через ILogger — невидимо в UI Logs
            // (#5 UX-беклогу: "у логах нуль інформації"). Публікуємо і сюди,
            // бо це саме той випадок, коли адмін реально нічого не побачить.
            logger.LogInformation("ZabbixPollerService: ZabbixUrl не налаштований — idle.");
            await mediator.Publish(AppLogEntryOccurred.Warning(LogSource,
                "Zabbix: Monitoring:ZabbixUrl не налаштований у appsettings.json — поллер не запущено."), stoppingToken);
            return;
        }

        bool zabbixMonitoringEnabled = await EvaluateMonitoringToggleAsync(stoppingToken);

        if (zabbixMonitoringEnabled && !credentials.HasZabbixCredentials)
        {
            await mediator.Publish(AppLogEntryOccurred.Info(LogSource,
                "Zabbix: credentials відсутні — очікуємо збереження через Settings API."), stoppingToken);
        }

        // Credentials є і моніторинг увімкнено — запускаємось повноцінно
        if (zabbixMonitoringEnabled && credentials.HasZabbixCredentials)
        {
            await LogStartedAsync(stoppingToken);
            _hasLoggedStart = true;

            if (!credentials.ZabbixUsesApiToken)
            {
                await AuthenticateAsync(stoppingToken).ConfigureAwait(false);
                if (_sessionToken is null && stoppingToken.IsCancellationRequested) return;
            }

            await PollAsync(stoppingToken).ConfigureAwait(false);
        }

        try
        {
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
                        "Zabbix: отримано сигнал пробудження — запускаємо позачерговий poll."), stoppingToken);
                }

                Interlocked.Exchange(ref _wakeUpCts, null);

                if (stoppingToken.IsCancellationRequested) break;

                // EDGE-CASE #1: перевірка toggle — НАЙПЕРША дія на кожній ітерації,
                // ЩЕ ДО перевірки HasZabbixCredentials.
                if (!await EvaluateMonitoringToggleAsync(stoppingToken))
                    continue;

                if (!credentials.HasZabbixCredentials)
                {
                    await mediator.Publish(AppLogEntryOccurred.Info(LogSource,
                        "Zabbix: credentials відсутні — poll пропущено."), stoppingToken);
                    continue;
                }

                // Перший успішний wake-up після старту без credentials — логуємо
                // запуск РАЗ (незалежно від auth-режиму: раніше цей виклик був
                // гейтований `!ZabbixUsesApiToken`, тож при API-token auth — саме
                // тим режимом, яким реально користується адмін — після збереження
                // токена через Settings у логах не з'являлось НІЧОГО аж до першого
                // успішного/невдалого poll'у. #5 UX-беклогу.
                if (!_hasLoggedStart)
                {
                    await LogStartedAsync(stoppingToken);
                    _hasLoggedStart = true;
                }

                if (!credentials.ZabbixUsesApiToken && _sessionToken is null)
                {
                    await AuthenticateAsync(stoppingToken).ConfigureAwait(false);
                    if (_sessionToken is null) continue;
                }

                await PollAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Нормальне завершення при StopAsync — ігноруємо.
        }
        finally
        {
            _wakeUpCts = null;
        }
    }

    // ── Authentication (user/password mode only) ─────────────────────────────

    private async Task AuthenticateAsync(CancellationToken ct)
    {
        try
        {
            var (username, password) = credentials.GetZabbix();
            _sessionToken = await client.LoginAsync(
                _settings.ZabbixUrl, username, password, ct)
                .ConfigureAwait(false);

            if (_sessionToken is null)
            {
                await mediator.Publish(AppLogEntryOccurred.Error(LogSource,
                    "Zabbix login failed — credentials видалено."), ct);
            }
            else
            {
                await mediator.Publish(AppLogEntryOccurred.Success(LogSource,
                    "Zabbix authentication successful."), ct);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ZabbixPollerService: login exception.");
            await mediator.Publish(AppLogEntryOccurred.Error(LogSource,
                $"Zabbix login exception: {ex.Message}"), ct);
        }
    }

    // ── Poll ─────────────────────────────────────────────────────────────────

    private const int MaxAuthRetries = 3;
    private int _consecutiveAuthFailures;

    // null = жодного poll'у ще не було. #5 UX-беклогу: успішні цикли раніше
    // взагалі нічого не писали в AppLogEntries — тиша виглядала ідентично і
    // при "все ок, просто 0 High/Disaster проблем", і при "інтеграція мертва".
    // Логуємо Success лише на ПЕРЕХОДІ у робочий стан (не щоцикл — 180с спам).
    private bool? _lastPollSucceeded;

    private async Task PollAsync(CancellationToken ct)
    {
        bool useApiToken = credentials.ZabbixUsesApiToken;

        var (_, tokenUsedForRequest) = credentials.GetZabbix();
        string auth = useApiToken ? tokenUsedForRequest : _sessionToken ?? string.Empty;

        try
        {
            var problems = await client.GetActiveProblemsAsync(
                _settings.ZabbixUrl, auth, useApiToken,
                WatchedSeverities, ct).ConfigureAwait(false);

            _consecutiveAuthFailures = 0;

            if (_lastPollSucceeded != true)
            {
                await mediator.Publish(AppLogEntryOccurred.Success(LogSource,
                    $"Zabbix: з'єднання працює, знайдено {problems.Count} активних проблем (severity High/Disaster)."), ct);
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
            var (_, currentTokenInVault) = credentials.GetZabbix();
            if (useApiToken
                && !string.IsNullOrWhiteSpace(currentTokenInVault)
                && currentTokenInVault != tokenUsedForRequest)
            {
                logger.LogInformation(
                    "Zabbix: токен оновлено під час запиту — ігноруємо помилку старого токена.");
                _consecutiveAuthFailures = 0;
                return;
            }

            // НЕ видаляємо токен — фоновий сервіс не має права стирати credentials.
            // Тільки юзер може видалити токен через Settings.
            _sessionToken = null;
            _consecutiveAuthFailures++;
            _lastPollSucceeded = false;

            await mediator.Publish(new ZabbixProblemsUpdatedOccurred(new ZabbixProblemsPayload(
                Problems: [],
                ErrorMessage: $"Токен відхилено Zabbix: {ex.Message}",
                FetchedAt: DateTimeOffset.Now)), ct);

            if (_consecutiveAuthFailures >= MaxAuthRetries)
            {
                await mediator.Publish(AppLogEntryOccurred.Warning(LogSource,
                    $"Zabbix: {MaxAuthRetries} послідовні цикли опитування з невалідним токеном. " +
                    $"Причина: {ex.Message} Оновіть токен у Settings."), ct);
            }
            else
            {
                await mediator.Publish(AppLogEntryOccurred.Warning(LogSource,
                    $"Zabbix: токен відхилено (цикл {_consecutiveAuthFailures}/{MaxAuthRetries}). " +
                    $"Причина: {ex.Message} Оновіть токен у Settings."), ct);
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
                ErrorMessage: $"Помилка зв'язку: {ex.Message}",
                FetchedAt: DateTimeOffset.Now)), ct);

            if (!useApiToken && ex is not HttpRequestException)
                await AuthenticateAsync(ct).ConfigureAwait(false);

            return;
        }
    }

    // ── On-demand (REST) ─────────────────────────────────────────────────────

    /// <summary>
    /// Живий опит Zabbix ЗАРАЗ, для початкового REST-знімка сторінки Zabbix
    /// Alerts (T6.2/Крок 11.1) — той самий принцип, що вже є в
    /// PingMonitorService.PingAllNowAsync. Навмисно НЕ чіпає
    /// _consecutiveAuthFailures/_lastPollSucceeded — це бухгалтерія фонового
    /// циклу опитування, окремий REST-запит не повинен впливати на її стан.
    /// </summary>
    public async Task<ZabbixProblemsPayload> GetActiveProblemsNowAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_settings.ZabbixUrl))
            return new ZabbixProblemsPayload(null, "Monitoring:ZabbixUrl не налаштований.", DateTimeOffset.Now);

        if (!credentials.HasZabbixCredentials)
            return new ZabbixProblemsPayload(null, "Credentials не збережені (Settings → Zabbix Token).", DateTimeOffset.Now);

        bool useApiToken = credentials.ZabbixUsesApiToken;
        var (username, secret) = credentials.GetZabbix();
        string auth = useApiToken ? secret : _sessionToken ?? string.Empty;

        if (!useApiToken && string.IsNullOrEmpty(auth))
        {
            auth = await client.LoginAsync(_settings.ZabbixUrl, username, secret, ct).ConfigureAwait(false) ?? string.Empty;
            if (string.IsNullOrEmpty(auth))
                return new ZabbixProblemsPayload(null, "Не вдалося авторизуватись у Zabbix.", DateTimeOffset.Now);
        }

        try
        {
            var problems = await client.GetActiveProblemsAsync(
                _settings.ZabbixUrl, auth, useApiToken, WatchedSeverities, ct).ConfigureAwait(false);
            return new ZabbixProblemsPayload(problems, null, DateTimeOffset.Now);
        }
        catch (Exception ex)
        {
            return new ZabbixProblemsPayload(null, $"Помилка зв'язку: {ex.Message}", DateTimeOffset.Now);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task LogStartedAsync(CancellationToken ct)
    {
        bool useApiToken = credentials.ZabbixUsesApiToken;
        logger.LogInformation(
            "ZabbixPollerService started. Auth: {Mode}. Interval: {Interval}s.",
            useApiToken ? "API Token" : "User/Password",
            _settings.ZabbixPollIntervalSeconds);

        await mediator.Publish(AppLogEntryOccurred.Info(LogSource,
            $"Zabbix poller started " +
            $"({(useApiToken ? "API token" : "user/password")} auth). " +
            $"Polling every {_settings.ZabbixPollIntervalSeconds}s."), ct);
    }
}
