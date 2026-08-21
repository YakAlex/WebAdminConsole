using AdminConsole.Domain.Events;
using AdminConsole.Domain.Models;
using MediatR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AdminConsole.Infrastructure.Remote;

/// <summary>
/// Читає Error/Critical записи з Windows Event Log (System + Application)
/// і публікує EventLogUpdatedOccurred.
///
/// Оптимізація: зберігає _lastRead timestamp між ітераціями.
/// Перший запуск — читає останні FetchCount помилок за весь час.
/// Наступні запуски — сканує лише записи новіші за _lastRead,
/// зупиняючись одразу як тільки зустрів старий запис (early exit).
/// Повідомлення не надсилається якщо нових записів немає.
///
/// T4.7: BackgroundService, тісний цикл (30с) — без Hangfire.
/// </summary>
public sealed class EventLogService(
    IMediator                 mediator,
    ILogger<EventLogService>  logger)
    : BackgroundService
{
    private const int FetchCount          = 20;
    private const int PollIntervalSeconds = 30;

    // Зберігає час останнього прочитаного запису.
    // null = перший запуск, читаємо весь InitialMaxScan назад.
    // non-null = інкрементальний режим, читаємо лише нове.
    private DateTimeOffset? _lastRead;

    // Кеш останнього повного знімку — потрібен для React-клієнтів, що
    // підключаються ПІСЛЯ того як цей BackgroundService уже опублікував
    // перше повідомлення (SignalR-подія "губиться", якщо нікого не було
    // підписано в момент Publish). REST-контролер (Фаза 6) читає це поле
    // напряму при початковому завантаженні, не чекаючи наступного PollIntervalSeconds.
    private readonly List<EventLogEntry> _lastSnapshot = new();
    private readonly object _snapshotLock = new();

    // Повертаємо знімок під lock — захищаємо від race з FetchAndPublishAsync,
    // яка пише з thread pool, поки читач (API/тест) читає паралельно.
    public IReadOnlyList<EventLogEntry> LastSnapshot
    {
        get
        {
            lock (_snapshotLock)
                return _lastSnapshot.ToList();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("EventLogService started.");

        await FetchAndPublishAsync(stoppingToken);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(PollIntervalSeconds),
                    stoppingToken).ConfigureAwait(false);

                if (stoppingToken.IsCancellationRequested) break;

                await FetchAndPublishAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Нормальне завершення при StopAsync — ігноруємо.
        }

        logger.LogInformation("EventLogService stopped.");
    }

    // ── Fetch ─────────────────────────────────────────────────────────────────

    private async Task FetchAndPublishAsync(CancellationToken ct)
    {
        try
        {
            // Знімаємо час ДО читання — щоб не пропустити записи
            // що з'явились поки ми читали.
            var readStart = DateTimeOffset.Now;
            var since     = _lastRead;

            var entries = await Task
                .Run(() => ReadErrors(since), ct)
                .ConfigureAwait(false);

            // Оновлюємо курсор лише якщо читання пройшло успішно
            _lastRead = readStart;

            // Оновлюємо кеш — при першому читанні просто зберігаємо,
            // при наступних додаємо нові записи на початок (найновіші зверху)
            // і ріжемо хвіст так само, як робив старий ViewModel.
            lock (_snapshotLock)
            {
                if (since is null)
                {
                    _lastSnapshot.Clear();
                    _lastSnapshot.AddRange(entries);
                }
                else if (entries.Count > 0)
                {
                    _lastSnapshot.InsertRange(0, entries);
                    if (_lastSnapshot.Count > FetchCount)
                        _lastSnapshot.RemoveRange(FetchCount, _lastSnapshot.Count - FetchCount);
                }
            }

            // Не надсилаємо повідомлення якщо нічого нового немає.
            // Виключення: перший запуск (since == null) — завжди надсилаємо
            // щоб UI отримав початковий стан.
            if (since is not null && entries.Count == 0)
            {
                logger.LogDebug("EventLogService: no new errors since {LastRead}.", since);
                return;
            }

            logger.LogDebug(
                "EventLogService: found {Count} new error(s) since {Since}.",
                entries.Count, since);

            await mediator.Publish(new EventLogUpdatedOccurred(entries), ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "EventLogService: failed to read event logs.");
        }
    }

    // ── Читання записів ───────────────────────────────────────────────────────
    // Винесено у WinEventLogReader — спільний для local (цей сервіс)
    // і remote (RemoteEventLogService) читання.
    private static List<EventLogEntry> ReadErrors(DateTimeOffset? since)
        => WinEventLogReader.ReadErrors(".", since);
}
