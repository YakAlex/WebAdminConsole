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
/// Опитує термінальні сервери через "quser /server:HOSTNAME".
///
/// ВАЖЛИВО: використовуємо доменне ім'я (ServerEntry.Name), а НЕ IP.
/// quser /server:TSVR3 — працює через Named Pipes / NetBIOS.
/// quser /server:192.168.x.x — не працює (RPC over TCP, зазвичай заблоковано).
///
/// Авторизація: бекенд-служба працює під виділеним доменним акаунтом
/// (DOMAIN\svc_adminconsole) з правами на цільових серверах — quser.exe
/// відпрацьовує в контексті самого процесу через Kerberos, без окремих
/// RDP credentials і без CredWrite/CredRead навколо кожного виклику.
///
/// T4.8: BackgroundService, тісний цикл — без Hangfire.
///
/// IRecipient&lt;MonitoringToggledMessage&gt;
/// (WeakReferenceMessenger.Default.Register у конструкторі) →
/// INotificationHandler&lt;MonitoringToggledOccurred&gt;
/// (DI-резолв MediatR).
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

    // Крок 1 (аудит "HTTP 0"): REST-знімок (GetSnapshotNowAsync) викликається
    // синхронно з браузера, що чекає на HTTP-відповідь — 30с/сервер (TimeoutMs)
    // із фонового циклу тут занадто довго й ризикує вперлись у зовнішній
    // таймаут проксі/браузера. Для REST-шляху ставимо явну, коротшу стелю.
    private const int    SnapshotTimeoutMs = 15_000;
    private CancellationTokenSource? _wakeUpCts;

    // Аудит-фікс (2026-08-22): троттлінг on-demand REST-знімку — вікно те
    // саме, що й фоновий цикл (RdpPollIntervalSeconds). Без цього кожен
    // захід на сторінку RDP Sessions незалежно запускав quser.exe проти
    // термінального сервера, незалежно від налаштованого інтервалу.
    private readonly OnDemandSnapshotThrottle<(IReadOnlyList<RdpSessionInfo> Sessions, int GlobalDailyPeak,
        string? LastLogoutUsername, string? LastLogoutServer, DateTimeOffset? LastLogoutAt)> _onDemandThrottle
        = new(TimeSpan.FromSeconds(settings.Value.RdpPollIntervalSeconds));

    // Кеш попереднього стану toggle (null = ще не перевіряли жодного разу).
    // Дозволяє логувати і слати MonitoringToggledOccurred лише на РЕАЛЬНІЙ
    // зміні стану, а не на кожному циклі опитування (anti-spam, edge-case #2).
    private bool? _monitoringWasEnabled;

    private readonly ConcurrentDictionary<string, Dictionary<int, RdpSessionInfo>>
        _previousSessions = new();
    private readonly ConcurrentDictionary<string, bool> _firstPollDone = new();

    // ── Глобальний стан для Overview
    private int             _globalDailyPeak;
    private DateTime        _peakResetDate;
    private string?         _lastLogoutUsername;
    private string?         _lastLogoutServer;
    private DateTimeOffset? _lastLogoutAt;
    private readonly object _stateLock = new();

    // ── Regex ────────────────────────────────────────────────────────────────
    // Формат WS2008R2 / WS2012+:
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
    /// Реагує на перемикання RDP-моніторингу в Settings.
    /// НЕ довіряє полю Enabled з повідомлення — це лише сигнал "прокинься
    /// і перевір джерело істини самостійно" (Pull, edge-case #2).
    /// Слугує для миттєвого відновлення опитування одразу після увімкнення,
    /// замість очікування до RdpPollIntervalSeconds.
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
    /// Перевіряє поточний стан RdpMonitoringEnabled (Pull з IAppSettingsRepository,
    /// джерело істини — AppSettings-рядок у БД) і, лише при РЕАЛЬНІЙ зміні
    /// відносно попередньої перевірки, логує подію та шле
    /// MonitoringToggledOccurred для синхронізації UI (edge-case #2 і #3).
    /// Виклик — щоразу перед credential-логікою (edge-case #1).
    /// </summary>
    private async Task<bool> EvaluateMonitoringToggleAsync(CancellationToken ct)
    {
        // IServiceScopeFactory замість прямої ін'єкції IAppSettingsRepository
        // (Scoped) — цей сервіс Singleton, той самий патерн, що MaintenanceService.
        AppSettings current;
        using (var scope = scopeFactory.CreateScope())
            current = await scope.ServiceProvider.GetRequiredService<IAppSettingsRepository>().GetAsync(ct);

        bool enabled = current.RdpMonitoringEnabled;

        if (_monitoringWasEnabled == enabled)
            return enabled; // стан не змінився — тиша, без спаму логів

        bool isColdStart = _monitoringWasEnabled is null;
        _monitoringWasEnabled = enabled;

        if (isColdStart)
        {
            // Аудит-фікс (2026-08-22, "Peak today: 0"): _globalDailyPeak — лише
            // в пам'яті, рестарт сервісу (деплой/перезавантаження) стирав його
            // до 0, навіть якщо сесія була активна й від'єдналась РАНІШЕ того ж
            // дня — до наступного рестарту ніхто вже не був онлайн, щоб пік
            // перерахувався заново. Відновлюємо з БД, якщо запис ще за сьогодні.
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
                "RDP моніторинг вимкнено в Settings."), ct);
            await mediator.Publish(new MonitoringToggledOccurred(MonitoredService.Rdp, false), ct);
        }
        else if (!isColdStart)
        {
            await mediator.Publish(AppLogEntryOccurred.Info(LogSource,
                "RDP моніторинг увімкнено — відновлюємо опитування."), ct);
            await mediator.Publish(new MonitoringToggledOccurred(MonitoredService.Rdp, true), ct);
        }

        return enabled;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_terminalServers.Count == 0)
        {
            // Раніше — лише ILogger (невидимо в UI Logs). Той самий клас
            // "тихої смерті", що й був у ZabbixPollerService (Крок 2, #5):
            // якщо жоден сервер у Servers не має Group == "Terminal Servers"
            // (типо/розбіжність при міграції appsettings.json), RDP-моніторинг
            // мовчки не запускається взагалі, назавжди.
            logger.LogInformation("RdpMonitorService: немає Terminal Servers — idle.");
            await mediator.Publish(AppLogEntryOccurred.Warning(LogSource,
                "RDP: жоден сервер у Servers не має Group=\"Terminal Servers\" — моніторинг не запущено. " +
                "Перевір appsettings.json."), stoppingToken);
            return;
        }

        foreach (var server in _terminalServers)
        {
            if (System.Net.IPAddress.TryParse(server.Name, out _))
            {
                logger.LogWarning(
                    "RdpMonitorService: сервер '{Name}' має IP-адресу замість доменного імені.",
                    server.Name);
                await mediator.Publish(AppLogEntryOccurred.Warning(LogSource,
                    $"Конфігурація: '{server.Name}' — це IP, а не ім'я. " +
                    $"quser може не працювати. Виправ Name у appsettings.json."), stoppingToken);
            }
        }

        bool rdpMonitoringEnabled = await EvaluateMonitoringToggleAsync(stoppingToken);

        if (rdpMonitoringEnabled)
        {
            await mediator.Publish(AppLogEntryOccurred.Info(LogSource,
                $"RDP monitor запущено — {_terminalServers.Count} сервер(ів). " +
                $"Використовуємо доменні імена для quser."), stoppingToken);
        }

        await PollAllServersAsync(stoppingToken);
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
                        TimeSpan.FromSeconds(_settings.RdpPollIntervalSeconds),
                        delayCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                    when (!stoppingToken.IsCancellationRequested)
                {
                    // Пробудження від MonitoringToggledOccurred (edge-case #2) —
                    // миттєвий позачерговий poll одразу після увімкнення моніторингу.
                    await mediator.Publish(AppLogEntryOccurred.Info(LogSource,
                        "RDP: отримано сигнал пробудження — запускаємо позачерговий poll."), stoppingToken);
                }

                Interlocked.Exchange(ref _wakeUpCts, null);

                if (stoppingToken.IsCancellationRequested) break;

                await PollAllServersAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _wakeUpCts = null;
        }
    }

    // ── Координація опитування ───────────────────────────────────────────────

    private async Task PollAllServersAsync(CancellationToken ct)
    {
        // Перевірка toggle — НАЙПЕРШИЙ рядок. Якщо RDP-моніторинг вимкнено —
        // жодного зайвого виклику quser.exe.
        if (!await EvaluateMonitoringToggleAsync(ct))
            return;

        var tasks = _terminalServers.Select(s => PollServerAsync(s, ct));
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    // ── Опитування одного сервера ────────────────────────────────────────────

    private Task PollServerAsync(ServerEntry server, CancellationToken ct)
        => PollServerOnceAsync(server, ct);

    private async Task PollServerOnceAsync(ServerEntry server, CancellationToken ct)
    {
        string hostname = server.Name;

        try
        {
            // quser.exe відпрацьовує в контексті самого процесу (сервіс
            // працює під DOMAIN\svc_adminconsole через Kerberos) — жодної
            // реєстрації credentials перед викликом не потрібно.
            var (output, error, exitCode) = await RunQuserAsync(hostname, ct).ConfigureAwait(false);

            string allText = (output + error).ToLowerInvariant();

            if (allText.Contains("logon failure") || allText.Contains("1326") ||
                allText.Contains("неверн") || allText.Contains("невірн"))
            {
                await LogSessionChangesAsync(server, [], ct);
                _previousSessions[server.IP] = new Dictionary<int, RdpSessionInfo>();
                await mediator.Publish(AppLogEntryOccurred.Warning(LogSource,
                    $"{hostname}: помилка автентифікації сервісного акаунту — перевірте права DOMAIN\\svc_adminconsole на цьому сервері."), ct);
                await mediator.Publish(new RdpSessionsUpdatedOccurred(await CreatePayloadAsync(
                    server.Name, server.IP, [], "Помилка автентифікації сервісного акаунту", ct)), ct);
                return;
            }

            if (allText.Contains("access is denied") || allText.Contains("access denied"))
            {
                await LogSessionChangesAsync(server, [], ct);
                _previousSessions[server.IP] = new Dictionary<int, RdpSessionInfo>();
                await mediator.Publish(AppLogEntryOccurred.Warning(LogSource,
                    $"{hostname}: Access Denied — перевірте права DOMAIN\\svc_adminconsole на цьому сервері."), ct);
                await mediator.Publish(new RdpSessionsUpdatedOccurred(await CreatePayloadAsync(
                    server.Name, server.IP, [], "Access Denied — перевірте права сервісного акаунту", ct)), ct);
                return;
            }

            if (allText.Contains("rpc server is unavailable") || allText.Contains("1722") || allText.Contains("0x000006ba"))
            {
                await mediator.Publish(AppLogEntryOccurred.Error(LogSource,
                    $"{hostname}: RPC недоступний. Переконайся що в appsettings.json вказано доменне ім'я (не IP)."), ct);
                await mediator.Publish(new RdpSessionsUpdatedOccurred(await CreatePayloadAsync(
                    server.Name, server.IP, [], "RPC недоступний — перевір ім'я сервера", ct)), ct);
                return;
            }

            if (string.IsNullOrWhiteSpace(output) || exitCode == 1)
            {
                bool noUsers = allText.Contains("no user") || allText.Contains("нет пользователей") || string.IsNullOrWhiteSpace(output);

                await LogSessionChangesAsync(server, [], ct);
                _previousSessions[server.IP] = [];

                await mediator.Publish(new RdpSessionsUpdatedOccurred(await CreatePayloadAsync(
                    server.Name, server.IP, [], noUsers ? null : $"Порожня відповідь (exit {exitCode})", ct)), ct);
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
            logger.LogWarning(ex, "RdpMonitorService: помилка при опитуванні {Server}", hostname);
            await mediator.Publish(AppLogEntryOccurred.Error(LogSource, $"{hostname}: {ex.GetType().Name}: {ex.Message}"), ct);
            await mediator.Publish(new RdpSessionsUpdatedOccurred(await CreatePayloadAsync(
                server.Name, server.IP, [], ex.Message, ct)), ct);
        }
    }

    // ── State Diffing ────────────────────────────────────────────────────────

    /// <summary>
    /// Порівнює поточний список сесій з попереднім знімком і логує тільки зміни.
    /// При першому poll для сервера — мовчки заповнює словник без логування,
    /// щоб не спамити "підключився" для вже існуючих сесій при старті програми.
    /// </summary>
    private async Task LogSessionChangesAsync(ServerEntry server, List<RdpSessionInfo> currentSessions, CancellationToken ct)
    {
        // Будуємо поточний знімок з валідними int SessionId
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

                // Аудит-фікс (2026-08-22, "Last logout: —"): раніше _lastLogoutUsername
                // оновлювався ЛИШЕ коли сесія повністю зникала з виводу quser (справжній
                // logoff). У реальному використанні набагато частіше користувач просто
                // закриває RDP-клієнт БЕЗ виходу — сесія лишається на сервері у стані
                // Disconnected (як і видно в таблиці SESSIONS), а "Last logout"/картка
                // Overview так і не дізнавались про це. Active → Disconnected — це саме
                // те, що звичайний користувач і мав на увазі під "logout".
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
    /// Намагається розрахувати тривалість сесії з рядка LogonTime від quser.
    /// quser повертає формат "dd.MM.yyyy HH:mm" або "MM/dd/yyyy h:mm AM/PM".
    /// Повертає порожній рядок якщо розпарсити не вдалось.
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
            return (partial, "Timeout: сервер не відповів за 30 секунд", -1);
        }
    }

    // ── Парсер виводу quser ──────────────────────────────────────────────────

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

    // ── Public API для TelegramBotService (Фаза 5)

    /// <summary>
    /// Живий знімок поточних RDP-сесій по кожному terminal-серверу (ключ — IP).
    /// _previousSessions вже ConcurrentDictionary — безпечно читати з будь-якого
    /// потоку. Значення копіюємо (.ToList()) щоб викликач не тримав посилання
    /// на внутрішній Dictionary, який поллер може оновити паралельно.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<RdpSessionInfo>> GetSnapshot()
        => _previousSessions.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<RdpSessionInfo>)kv.Value.Values.ToList());

    /// <summary>
    /// Живий опит усіх terminal-серверів (quser) ЗАРАЗ + агрегований знімок —
    /// для початкового REST-завантаження сторінки RDP Sessions (Фаза 10,
    /// Крок 11.2 аудиту — раніше сторінка мала лише SignalR-потік). Той самий
    /// виклик PollAllServersAsync, що й фоновий цикл — публікує ті самі
    /// RdpSessionsUpdatedOccurred-події (клієнт, що ініціював запит, побачить
    /// дані і з відповіді, і з SignalR), і так само поважає RdpMonitoringEnabled.
    ///
    /// Аудит-фікс (2026-08-22): _onDemandThrottle обмежує ЧАСТОТУ викликів
    /// до RdpPollIntervalSeconds — повторний запит у межах вікна (F5,
    /// декілька відкритих вкладок) повертає щойно отриманий знімок замість
    /// нового quser.exe проти сервера.
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
            $"RDP: REST-знімок запит отримано — опитуємо {_terminalServers.Count} сервер(ів) " +
            $"(ліміт {SnapshotTimeoutMs / 1000}с)."), ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(SnapshotTimeoutMs);

        await PollAllServersAsync(timeoutCts.Token).ConfigureAwait(false);
        sw.Stop();

        if (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            await mediator.Publish(AppLogEntryOccurred.Warning(LogSource,
                $"RDP: REST-знімок перевищив ліміт {SnapshotTimeoutMs / 1000}с ({sw.ElapsedMilliseconds}мс) — " +
                "частина серверів могла не встигнути відповісти, повертаємо останні відомі дані."), ct);
        }
        else
        {
            await mediator.Publish(AppLogEntryOccurred.Info(LogSource,
                $"RDP: REST-знімок завершено за {sw.ElapsedMilliseconds}мс."), ct);
        }

        var sessions = _previousSessions.Values.SelectMany(d => d.Values).ToList();
        lock (_stateLock)
        {
            return (sessions, _globalDailyPeak, _lastLogoutUsername, _lastLogoutServer, _lastLogoutAt);
        }
    }

    /// <summary>
    /// Розраховує глобальний пік і формує Payload.
    /// Це гарантує, що клієнт отримає консистентні історичні дані.
    /// Асинхронна — при НОВОМУ піку одразу персистить його в AppSettings
    /// (поза lock, бо lock не може огортати await), щоб "Peak today" пережив
    /// рестарт сервісу протягом тієї ж доби (див. EvaluateMonitoringToggleAsync).
    /// </summary>
    private async Task<RdpSessionsPayload> CreatePayloadAsync(
        string serverName, string serverIp, IReadOnlyList<RdpSessionInfo> sessions, string? errorMessage,
        CancellationToken ct)
    {
        bool peakChanged;
        int  peakToPersist;
        DateTime dateToPersist;
        RdpSessionsPayload payload;

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

            peakToPersist  = _globalDailyPeak;
            dateToPersist  = _peakResetDate;

            payload = new RdpSessionsPayload(
                serverName, serverIp, sessions, errorMessage,
                _globalDailyPeak, _lastLogoutUsername, _lastLogoutServer, _lastLogoutAt);
        }

        if (peakChanged)
            await PersistDailyPeakAsync(peakToPersist, dateToPersist, ct).ConfigureAwait(false);

        return payload;
    }

    private async Task PersistDailyPeakAsync(int peak, DateTime date, CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IAppSettingsRepository>();
            var current = await repo.GetAsync(ct);
            current.RdpDailyPeak     = peak;
            current.RdpDailyPeakDate = date;
            await repo.SaveAsync(current, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "RdpMonitorService: не вдалось зберегти RdpDailyPeak у AppSettings.");
        }
    }
}
