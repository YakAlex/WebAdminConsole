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
/// Відслідковує переходи Online↔Offline для кожного сервера.
/// Обробляє PingBatchResultOccurred (MediatR notification замість
/// IRecipient&lt;PingBatchResultMessage&gt;). Зберігає інциденти через
/// IDowntimeRepository (EF Core, Фаза 2). Публікує UptimeUpdatedOccurred
/// при кожній зміні.
///
/// T4.3: анти-флапінг (Pending→Confirmed) і reconciliation-логіка перенесені
/// БЕЗ ЗМІН. Debounced ScheduleSave()/dirty-months (файлове оптимізаційне
/// накопичення перед File.Move) прибрано — з EF Core кожна зміна пишеться
/// одразу окремим await UpsertAsync/DeleteAsync одразу ПІСЛЯ виходу з-під
/// _lock (сам lock лишається 1:1 навколо in-memory мутацій, як і раніше;
/// різниця лише в тому, що робиться ПІСЛЯ lock — раніше debounced Task.Run,
/// тепер прямий await). Це заразом усуває весь клас "втрачено останні 500мс
/// перед закриттям" — final-flush StopAsync більше не потрібен, кожна зміна
/// вже на диску (у БД) в момент мутації.
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

    // IServiceScopeFactory замість прямої ін'єкції IDowntimeRepository —
    // репозиторій Scoped, сервіс Singleton (той самий патерн, що MaintenanceService).
    private async Task<T> WithRepositoryAsync<T>(Func<IDowntimeRepository, Task<T>> action)
    {
        using var scope = scopeFactory.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<IDowntimeRepository>());
    }

    private Task WithRepositoryAsync(Func<IDowntimeRepository, Task> action) =>
        WithRepositoryAsync(async r => { await action(r); return true; });

    /// Поточний статус кожного IP (для визначення переходів)
    private readonly Dictionary<string, PingStatus> _lastStatus = new();

    /// <summary>
    /// IP-адреси, для яких уже прийшов ПЕРШИЙ реальний (не Checking/Unknown)
    /// результат пінгу цієї сесії. Потрібно для reconciliation при старті:
    /// одразу після рестарту _lastStatus порожній, тому звичайна перевірка
    /// "prev == Offline" ніколи не спрацює для сервера, що відновився, поки
    /// застосунок був вимкнений — доводиться один раз (саме один, далі
    /// нормальна логіка prev==Offline вже коректно працює) звірити напряму
    /// з персистентним _records, чи немає там "осиротілого" відкритого
    /// інциденту для цього IP.
    /// </summary>
    private readonly HashSet<string> _reconciledIps = new();

    // Всі інциденти в пам'яті (поточна сесія + завантажені з БД)
    private readonly List<DowntimeRecord> _records = new();
    private readonly object               _lock    = new();

    /// <summary>
    /// Сервери, які зараз Offline, але ще не "визріли" до MinIncidentDurationSeconds.
    /// Живе виключно в пам'яті — жодного DowntimeRecord, жодного upsert,
    /// жодного PublishSnapshot, поки інцидент не підтвердиться і не буде
    /// перенесений у _records. Дозволяє повністю уникнути зайвого I/O та
    /// UI-мерехтіння для коротких мережевих "миготінь".
    /// </summary>
    private readonly Dictionary<string, PendingOffline> _pendingOffline = new();

    private readonly record struct PendingOffline(
        DateTimeOffset FellAt, string ServerName, string Group);

    private const string LogSource = "UptimeTracker";

    // ── Lifecycle: гарантоване завантаження ДО старту наступних сервісів ───

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await LoadFromDbAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await PublishSnapshotAsync(stoppingToken);
        logger.LogInformation("UptimeTrackerService started.");
        await mediator.Publish(AppLogEntryOccurred.Info(LogSource,
            "Uptime tracker started — відстеження переходів Online/Offline запущено."), stoppingToken);
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

    private async Task HandleMaintenanceStartedAsync(MaintenanceWindow window, CancellationToken ct)
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
            $"Відкриті інциденти для {window.DisplayName} закрито через Maintenance Mode."), ct);
    }

    /// <summary>
    /// ФІКС (перенесено без змін): без цього _lastStatus[ip] лишався Offline
    /// назавжди, якщо сервер не піднявся до кінця вікна — жоден наступний
    /// Offline-пінг більше не сприймався як "новий перехід" (prev вже
    /// дорівнював Offline), тому DowntimeRecord ніколи не створювався для
    /// періоду ПІСЛЯ вікна.
    ///
    /// Скидаємо саме на Online (а не Unknown, як у PingMonitorService) —
    /// перехід Unknown/Checking → Offline у ЦЬОМУ сервісі навмисно не створює
    /// _pendingOffline (фільтр "стартового шуму"), тому Unknown відтворив би
    /// той самий баг. Online → Offline — звичайна, вже перевірена гілка:
    /// наступний реальний Offline-пінг коректно "падає" в _pendingOffline
    /// з FellAt = момент виявлення. Якщо сервер вже онлайн — безпечний no-op.
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

    public async Task Handle(PingBatchResultOccurred notification, CancellationToken ct)
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

                // Add() повертає true, якщо цей IP бачимо ВПЕРШЕ з реальним
                // (не Checking/Unknown) статусом цієї сесії — саме цей момент
                // потребує звірки з БД (див. гілку Online нижче).
                bool isFirstRealStatusThisSession = _reconciledIps.Add(result.IP);

                if (result.Status == PingStatus.Offline)
                {
                    bool underMaintenance = maintenance.IsUnderMaintenance(result.IP, result.Group);

                    if (prev != PingStatus.Offline
                        && prev is not PingStatus.Unknown and not PingStatus.Checking
                        && !underMaintenance)
                    {
                        // Свіже падіння — НЕ пишемо DowntimeRecord одразу.
                        // Кладемо в pending і чекаємо MinIncidentDurationSeconds,
                        // перш ніж це стане "офіційним" інцидентом.
                        _pendingOffline[result.IP] =
                            new PendingOffline(DateTimeOffset.Now, result.Name, result.Group);
                    }
                    else if (!underMaintenance &&
                             _pendingOffline.TryGetValue(result.IP, out var pending))
                    {
                        // Сервер досі Offline — перевіряємо чи вже минув поріг.
                        var elapsed = DateTimeOffset.Now - pending.FellAt;
                        if (_settings.MinIncidentDurationSeconds <= 0
                            || elapsed.TotalSeconds >= _settings.MinIncidentDurationSeconds)
                        {
                            // Інцидент "визрів" — тільки тепер створюємо запис,
                            // пишемо в БД і показуємо в UI. FellAt лишається
                            // справжнім часом падіння, а не моментом промоції.
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
                        // Звичайний, уже перевірений часом шлях: сервер впав і
                        // піднявся, поки застосунок ПРАЦЮВАВ — _lastStatus
                        // коректно відстежив обидва переходи цієї сесії.
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
                        // ФІКС: перший реальний пінг цього IP цієї сесії, і при
                        // цьому prev НЕ Offline (бо _lastStatus щойно після
                        // рестарту порожній — звичайна перевірка вище ніколи
                        // б не спрацювала). Звіряємось напряму з БД (уже
                        // завантаженою в _records при старті): якщо там лежить
                        // незакритий інцидент для цього IP — сервер явно
                        // відновився, поки застосунок був вимкнений.
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
                $"{name} ({ip}) уже ONLINE після перезапуску " +
                $"застосунку — закрито інцидент, що почався {fellAt:dd.MM HH:mm}."), ct);

        if (!changed) return;

        await PublishSnapshotAsync(ct);
    }

    // ── Public API для UptimeViewModel/React (Фаза 6) ───────────────────────

    /// <summary>
    /// ФІКС (перенесено без змін): повертає ГЛИБОКІ копії, а не референси на
    /// живі об'єкти з _records. DowntimeRecord.RecoveredAt/ClosedByMaintenance
    /// мутуються з фонового потоку в Handle(PingBatchResultOccurred) без
    /// зв'язку з тим, хто читає знімок ззовні _lock. SlaReportService.Generate()
    /// читає той самий запис (ClippedDuration) кілька разів незалежно — без
    /// заморожування знімка ці виклики можуть побачити різні значення для
    /// одного інциденту в межах одного звіту.
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
    /// Видаляє один запис з пам'яті та БД.
    /// Якщо запис активний (!IsResolved) — скидає _lastStatus[IP] на Online,
    /// щоб трекер коректно відстежував наступний перехід для цього сервера.
    /// Викликається з майбутнього Uptime API-контролера (Фаза 6).
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
                    "DeleteRecord: запис {Server} ({Ip}) / {FellAt} не знайдено — можливо, вже видалено.",
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
            $"Інцидент видалено вручну: {record.ServerName} ({record.ServerIp}), " +
            $"впав {record.FellAt:dd.MM HH:mm:ss}."), ct);
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
            $"Очищено {removed} завершених інцидентів з історії."), ct);
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
                "UptimeTrackerService: завантажено {Count} записів.", loaded.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "UptimeTrackerService: помилка завантаження з БД.");
        }
    }

    private async Task PublishSnapshotAsync(CancellationToken ct)
    {
        IReadOnlyList<DowntimeRecord> snapshot;
        lock (_lock) snapshot = _records.Select(CloneRecord).ToList();
        await mediator.Publish(new UptimeUpdatedOccurred(snapshot), ct);
    }
}
