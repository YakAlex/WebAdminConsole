using System.Collections.Concurrent;
using AdminConsole.Domain.Abstractions;
using AdminConsole.Domain.Events;
using AdminConsole.Domain.Models;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AdminConsole.Infrastructure.Monitoring;

/// <summary>
/// Керує вікнами планового обслуговування (Maintenance Windows).
///
/// Гібридна модель Pull + Push:
///   - Pull: поллери (Ping, RDP) синхронно викликають IsUnderMaintenance
///     перед відправкою Warning/Error повідомлень — без затримки MediatR.
///   - Push: StartMaintenance / автозавершення в ExecuteAsync шлють
///     MaintenanceChangedOccurred — UptimeTracker закриває інциденти,
///     PingMonitor перегенеровує алерти, SignalR оновлює UI миттєво.
///
/// Сховище — ConcurrentDictionary, бо читається одночасно з кількох
/// фонових потоків (Ping/RDP поллери на кожному циклі) і пишеться
/// з майбутнього Settings API (UI) та з власного фонового циклу автозавершення.
///
/// T4.1: мігрується ПЕРШИМ серед stateful-сервісів — PingMonitorService і
/// UptimeTrackerService залежать від нього. Заміна LoadFromDisk() у
/// конструкторі (синхронний виклик, більше неможливий з async-репозиторієм)
/// на await у StartAsync, який Generic Host гарантовано await'ить ДО того,
/// як стартує наступний зареєстрований IHostedService.
///
/// IServiceScopeFactory замість прямої ін'єкції IMaintenanceRepository:
/// репозиторій — Scoped (прив'язаний до Scoped AdminConsoleDbContext), а
/// цей сервіс — Singleton. DI забороняє Singleton напряму тримати Scoped-
/// залежність (лише через фабрику скоупів) — стандартний, задокументований
/// Microsoft патерн для BackgroundService, якому потрібен EF Core.
/// </summary>
public sealed class MaintenanceService(
    IMediator                    mediator,
    IServiceScopeFactory         scopeFactory,
    ILogger<MaintenanceService>  logger)
    : BackgroundService
{
    private readonly ConcurrentDictionary<string, MaintenanceWindow> _windows = new();

    private const string LogSource            = "Maintenance";
    private const int    CheckIntervalSeconds = 30;

    private async Task<T> WithRepositoryAsync<T>(Func<IMaintenanceRepository, Task<T>> action)
    {
        using var scope = scopeFactory.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<IMaintenanceRepository>());
    }

    private Task WithRepositoryAsync(Func<IMaintenanceRepository, Task> action) =>
        WithRepositoryAsync(async r => { await action(r); return true; });

    // ── Lifecycle: гарантоване завантаження ДО старту будь-якого іншого сервісу ──

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await LoadFromDbAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    // ── Pull API — викликається з фонових поллерів ─────────────────────────

    public bool IsUnderMaintenance(string serverIp, string group)
    {
        var now = DateTimeOffset.Now;

        if (_windows.TryGetValue(serverIp, out var w) && w.IsActiveAt(now))
            return true;

        if (!string.IsNullOrEmpty(group) &&
            _windows.TryGetValue($"group:{group}", out var gw) && gw.IsActiveAt(now))
            return true;

        return false;
    }

    /// <summary>Повертає активне вікно (для UI — показати Reason/To у тултипі).</summary>
    public MaintenanceWindow? GetActiveWindow(string serverIp, string group)
    {
        var now = DateTimeOffset.Now;

        if (_windows.TryGetValue(serverIp, out var w) && w.IsActiveAt(now))
            return w;

        if (!string.IsNullOrEmpty(group) &&
            _windows.TryGetValue($"group:{group}", out var gw) && gw.IsActiveAt(now))
            return gw;

        return null;
    }

    /// <summary>
    /// Усі активні вікна обслуговування прямо зараз. Публічний read-only
    /// знімок — потрібен TelegramBotService (Фаза 5) для команди/кнопки
    /// "Обслуговування", без потреби окремо кешувати стан через MediatR.
    /// </summary>
    public IReadOnlyList<MaintenanceWindow> GetActiveWindows()
    {
        var now = DateTimeOffset.Now;
        return _windows.Values.Where(w => w.IsActiveAt(now)).ToList();
    }

    // ── Push API — викликається з майбутнього Settings/Maintenance API ─────

    public async Task StartMaintenanceAsync(MaintenanceWindow window, CancellationToken ct = default)
    {
        _windows[window.Key] = window;
        await WithRepositoryAsync(r => r.UpsertAsync(window, ct));

        logger.LogInformation(
            "Maintenance розпочато: {Key}, до {To}",
            window.Key, window.To?.ToString() ?? "без обмеження");

        await mediator.Publish(AppLogEntryOccurred.Info(LogSource,
            $"Maintenance розпочато для {window.DisplayName}: " +
            $"{(string.IsNullOrWhiteSpace(window.Reason) ? "без причини" : window.Reason)} " +
            $"({(window.To is { } to ? $"до {to.ToLocalTime():dd.MM HH:mm}" : "без обмеження часу")})."), ct);

        await mediator.Publish(new MaintenanceChangedOccurred(MaintenanceAction.Started, window), ct);
    }

    /// <summary>Дострокове завершення вручну (адмін відновив сервер раніше графіка).</summary>
    public async Task EndMaintenanceEarlyAsync(string key, CancellationToken ct = default)
    {
        if (!_windows.TryRemove(key, out var window))
            return;

        await WithRepositoryAsync(r => r.RemoveAsync(key, ct));

        await mediator.Publish(AppLogEntryOccurred.Info(LogSource,
            $"Maintenance для {window.DisplayName} завершено вручну."), ct);

        await mediator.Publish(new MaintenanceChangedOccurred(MaintenanceAction.Ended, window), ct);
    }

    /// <summary>
    /// Знімає ВСІ активні вікна обслуговування (включно з "без обмеження часу")
    /// і очищує їх з БД. Викликається при graceful shutdown хосту —
    /// Maintenance Mode свідомо не переживає перезапуск застосунку: інакше
    /// забуте "без обмеження" вікно могло б мовчки придушувати алерти
    /// тижнями після того, як службу просто перезапустили.
    /// </summary>
    public async Task ClearAllOnShutdownAsync(CancellationToken ct = default)
    {
        var windows = _windows.Values.ToList();
        if (windows.Count == 0) return;

        _windows.Clear();
        await WithRepositoryAsync(r => r.RemoveAllAsync(ct));

        foreach (var window in windows)
            await mediator.Publish(new MaintenanceChangedOccurred(MaintenanceAction.Ended, window), ct);

        logger.LogInformation(
            "MaintenanceService: {Count} вікон(о) обслуговування знято при закритті застосунку.",
            windows.Count);
    }

    // ── Фонове автозавершення прострочених вікон ────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("MaintenanceService started. {Count} активних вікон завантажено.",
            _windows.Count);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(CheckIntervalSeconds), stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }

            var now = DateTimeOffset.Now;
            var expired = _windows.Where(kv => kv.Value.To is not null && kv.Value.To < now).ToList();
            if (expired.Count == 0) continue;

            foreach (var (key, window) in expired)
            {
                if (!_windows.TryRemove(key, out _)) continue;

                await WithRepositoryAsync(r => r.RemoveAsync(key, stoppingToken));

                await mediator.Publish(AppLogEntryOccurred.Info(LogSource,
                    $"Maintenance для {window.DisplayName} завершено (час вийшов)."), stoppingToken);

                await mediator.Publish(new MaintenanceChangedOccurred(MaintenanceAction.Ended, window), stoppingToken);
            }
        }
    }

    // ── Startup load ─────────────────────────────────────────────────────────

    private async Task LoadFromDbAsync(CancellationToken ct)
    {
        try
        {
            var windows = await WithRepositoryAsync(r => r.LoadAllAsync(ct));
            var now = DateTimeOffset.Now;

            foreach (var w in windows)
                if (w.To is null || w.To >= now)
                    _windows[w.Key] = w;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "MaintenanceService: помилка завантаження з БД.");
        }
    }
}
