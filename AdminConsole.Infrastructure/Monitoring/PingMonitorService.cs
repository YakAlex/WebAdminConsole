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
/// T4.2: BackgroundService, AddSingleton — БЕЗ Hangfire (тісний dual-loop
/// цикл секундного порядку, правило Hangfire vs BackgroundService).
///
/// IRecipient&lt;MaintenanceChangedMessage&gt; (реєстрація в конструкторі,
/// WeakReferenceMessenger) → INotificationHandler&lt;MaintenanceChangedOccurred&gt;
/// (клас резолвиться й викликається через DI, MediatR fan-out).
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

    // ── Стан статусів ────────────────────────────────────────────────────────

    // Єдине джерело правди про поточний статус кожного IP.
    // ConcurrentDictionary — читається і пишеться з обох циклів паралельно.
    private readonly ConcurrentDictionary<string, PingStatus> _previousStatus = new();

    // ── Throttle ─────────────────────────────────────────────────────────────

    // Основний цикл: до 10 паралельних пінгів (15 серверів → 10+5)
    private readonly SemaphoreSlim _mainThrottle     = new(10);

    // Recovery loop: окремий throttle на 5 слотів.
    // Не ділимо з основним — recovery не блокується основним циклом
    // навіть якщо всі 10 слотів зайняті.
    private readonly SemaphoreSlim _recoveryThrottle = new(5);

    /// <summary>
    /// Per-IP замок: якщо /ping (on-demand з Telegram) і фоновий цикл
    /// (main/recovery loop) намагаються опитати ОДИН і той самий сервер
    /// одночасно — без цього замка обидва виклики незалежно читають/пишуть
    /// _previousStatus[ip] через GetOrAdd+TryUpdate (CAS), що НЕ пошкоджує
    /// сам словник, але може подвоїти або загубити один із Warning/Error
    /// логів про перехід статусу через інтерлівінг двох перевірок стану.
    /// Серіалізуємо саме на рівні "один сервер" — різні сервери й далі
    /// пінгуються повністю паралельно між собою.
    /// </summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _perServerLocks = new();

    private SemaphoreSlim GetServerLock(string ip) =>
        _perServerLocks.GetOrAdd(ip, _ => new SemaphoreSlim(1, 1));

    // Аудит-фікс (2026-08-22): троттлінг on-demand /ping (REST + Telegram
    // /ping) — вікно те саме, що й основний цикл (PingIntervalSeconds).
    private readonly OnDemandSnapshotThrottle<IReadOnlyList<PingResult>> _onDemandThrottle =
        new(TimeSpan.FromSeconds(settings.Value.PingIntervalSeconds));

    // ── Константи ────────────────────────────────────────────────────────────

    private const int    PingTimeoutMs        = 2000;
    private const string LogSource            = "PingMonitor";
    private const int    MinOfflineIntervalSec = 5; // захист від некоректного appsettings

    // ── INotificationHandler<MaintenanceChangedOccurred> ────────────────────

    public Task Handle(MaintenanceChangedOccurred notification, CancellationToken ct)
    {
        if (notification.Action != MaintenanceAction.Ended) return Task.CompletedTask;

        // Скидаємо previousStatus для зачеплених серверів на Unknown —
        // наступний цикл пінгу сприйме поточний Offline (якщо сервер
        // не встиг піднятись вчасно) як "перехід з Unknown", що вже
        // існуючою гілкою коду генерує Warning — без окремої логіки
        // "примусового алерту".
        var affected = notification.Window.TargetGroup is not null
            ? _servers.Where(s => s.Group.Equals(notification.Window.TargetGroup,
                StringComparison.OrdinalIgnoreCase))
            : _servers.Where(s => s.IP == notification.Window.ServerIp);

        foreach (var s in affected)
            _previousStatus[s.IP] = PingStatus.Unknown;

        return Task.CompletedTask;
    }

    // ── BackgroundService ────────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Валідація налаштувань — захист від некоректного appsettings.json
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

        // LinkedCts дозволяє одному циклу скасувати інший при падінні.
        // Без цього якщо MainLoop впаде з винятком — RecoveryLoop
        // продовжує крутитись нескінченно і навпаки.
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
            logger.LogError(ex, "PingMonitorService: критична помилка циклу.");
        }

        logger.LogInformation("PingMonitorService stopped.");
        await mediator.Publish(AppLogEntryOccurred.Warning(LogSource, "Ping monitor stopped."), CancellationToken.None);
    }

    // ── Основний цикл (всі сервери, кожні N секунд) ──────────────────────────

    private async Task RunMainLoopAsync(CancellationToken ct)
    {
        bool firstRun = true;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Перша ітерація — одразу пінгуємо без затримки.
                // Наступні — чекаємо PingIntervalSeconds.
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
            // Нормальне завершення при StopAsync — ігноруємо.
        }
    }

    // ── Recovery loop (тільки Offline сервери, кожні M секунд) ──────────────

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

    // ── Guard для циклів ──────────────────────────────────────────────────────

    /// <summary>
    /// Обгортка над циклом: якщо цикл впав з неочікуваним винятком —
    /// скасовує linkedCts щоб зупинити паралельний цикл,
    /// потім перекидає виняток щоб Task.WhenAll його побачив.
    /// OperationCanceledException — нормальне завершення, ігнорується.
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
            // Падіння одного циклу → зупиняємо другий
            linkedCts.Cancel();
            throw;
        }
    }

    // ── Спільна логіка пінгування ─────────────────────────────────────────────

    /// <summary>
    /// Пінгує список серверів паралельно через вказаний throttle,
    /// збирає результати і публікує один PingBatchResultOccurred.
    /// Використовується і основним циклом і recovery loop —
    /// різниця тільки у списку серверів і throttle.
    /// </summary>
    private async Task PingServersAsync(
        IEnumerable<ServerEntry> servers,
        SemaphoreSlim            throttle,
        CancellationToken        ct)
    {
        // Локальний bag — не поле класу.
        // Кожен виклик PingServersAsync має свій ізольований bag,
        // тому основний і recovery цикли не можуть перезаписати один одного.
        var bag = new ConcurrentBag<PingResult>();

        var tasks = servers.Select(s => PingSingleServerAsync(s, throttle, bag, ct));
        await Task.WhenAll(tasks).ConfigureAwait(false);

        if (ct.IsCancellationRequested) return;

        // Публікуємо навіть якщо bag порожній (всі OperationCanceled) —
        // перевірка вище це покриває.
        var results = bag.ToArray();
        if (results.Length == 0) return;

        await mediator.Publish(new PingBatchResultOccurred(
            new PingBatchPayload(
                Results:          results,
                CycleCompletedAt: DateTimeOffset.Now)), ct);
    }

    // ── Пінг одного сервера ───────────────────────────────────────────────────

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
            acquired = true;  // слот захоплено — тепер Release() безпечний

            // Серіалізація саме для цього IP — якщо цей сервер уже
            // пінгується іншим викликом (main loop / recovery loop / /ping),
            // чекаємо на його завершення перед тим, як читати/писати
            // _previousStatus[ip] і слати транзиційні логи.
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
                                    $"{server.Name} ({server.IP}) недоступний при старті."), ct);
                            else
                                await mediator.Publish(AppLogEntryOccurred.Error(LogSource,
                                    $"{server.Name} ({server.IP}) went OFFLINE."), ct);
                        }
                        // Під maintenance — жодного Warning/Error, але статус
                        // все одно оновлюється (PingResult нижче), UI покаже
                        // Offline + бейдж 🔧 замість тривоги.
                    }
                    // Checking/Unknown → Online: тихо, без логу — не спам при старті.
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

    // ── Public API для TelegramBotService (Фаза 5)

    /// <summary>
    /// Живий знімок поточного статусу всіх серверів прямо зараз.
    /// ConcurrentDictionary вже є єдиним джерелом правди (_previousStatus),
    /// тому це тонкий read-only метод без додаткової синхронізації.
    /// Дозволяє боту відповідати коректно навіть у перші секунди після старту,
    /// не покладаючись лише на PingBatchResultOccurred (яка ще могла не прийти).
    /// </summary>
    public IReadOnlyDictionary<string, PingStatus> GetSnapshot()
        => _previousStatus.ToDictionary(kv => kv.Key, kv => kv.Value);

    /// <summary>
    /// Пінгує ВСІ сервери прямо зараз, поза звичайним циклом (REST GET
    /// /api/ping при заході на Overview/Ping + команда /ping бота — "живий"
    /// запит на вимогу). Перевикористовує ту саму PingSingleServerAsync —
    /// тобто:
    ///  - оновлює _previousStatus (той самий стан, що бачить UI);
    ///  - шле ті самі Warning/Error/Success логи при зміні статусу;
    ///  - шле PingBatchResultOccurred — UI Ping Dashboard оновиться теж.
    /// Ділить throttle з основним циклом (_mainThrottle) — жодного
    /// окремого "паралельного" навантаження на мережу понад заплановане.
    /// Заразом (аудит-фікс 2026-08-22): _onDemandThrottle обмежує ЧАСТОТУ
    /// самих викликів до PingIntervalSeconds — повторний REST/Telegram-запит
    /// у межах вікна повертає щойно отриманий результат замість нового
    /// реального ping-опитування.
    /// </summary>
    public Task<IReadOnlyList<PingResult>> PingAllNowAsync(CancellationToken ct) =>
        _onDemandThrottle.GetOrRunAsync(PingAllNowInternalAsync, ct);

    private async Task<IReadOnlyList<PingResult>> PingAllNowInternalAsync(CancellationToken ct)
    {
        var bag = new ConcurrentBag<PingResult>();

        var tasks = _servers.Select(s => PingSingleServerAsync(s, _mainThrottle, bag, ct));
        await Task.WhenAll(tasks).ConfigureAwait(false);

        // Аудит-фікс (2026-08-22, троттлінг on-demand): PingSingleServerAsync
        // тихо ковтає власне OperationCanceledException (щоб один скасований
        // сервер не валив увесь Task.WhenAll) — тому скасування зовнішнього ct
        // (клієнт відключився під час опитування) інакше пройшло б непоміченим
        // і НЕПОВНИЙ/порожній bag кешувався б _onDemandThrottle як валідний
        // результат на весь PingIntervalSeconds для всіх наступних викликів.
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
        // Обидва SemaphoreSlim містять внутрішній WaitHandle — звільняємо обидва.
        _mainThrottle.Dispose();
        _recoveryThrottle.Dispose();
        foreach (var l in _perServerLocks.Values) l.Dispose();
        base.Dispose();
    }
}
