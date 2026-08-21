using AdminConsole.Domain.Abstractions;
using AdminConsole.Domain.Events;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace AdminConsole.Infrastructure.Monitoring;

/// <summary>
/// T4.13: заміна FileLoggerService (видалено повністю) — замість
/// ConcurrentQueue + окремого flush-циклу в rolling app-YYYY-MM-DD.log,
/// кожен AppLogEntryOccurred одразу пишеться в таблицю AppLogEntries
/// через IAppLogRepository. Прибирає весь клас проблем із multi-file
/// merge/"хвіст найновішого файлу при старті" — тепер це просто
/// ORDER BY Timestamp DESC LIMIT :take в SQL (LogsController, Фаза 3).
///
/// MediatR fan-out означає, що цей хендлер працює паралельно з
/// SignalRBroadcastHandler (Api, Фаза 3) для ТІЄЇ Ж події — падіння
/// одного не гасить інший.
///
/// IServiceScopeFactory замість прямої ін'єкції IAppLogRepository: цей
/// хендлер реєструється Singleton (той самий root-провайдер, що й решта
/// Infrastructure-хендлерів — MediatR, впроваджений у Singleton-сервіс
/// на кшталт UptimeTrackerService, викликає Publish через ЗАХОПЛЕНИЙ
/// root-провайдер, тому НІЯКИЙ хендлер, якого він резолвить, не може
/// бути Scoped — навіть якщо сам хендлер синтаксично не Singleton).
/// </summary>
public sealed class AppLogPersistenceHandler(IServiceScopeFactory scopeFactory)
    : INotificationHandler<AppLogEntryOccurred>
{
    public async Task Handle(AppLogEntryOccurred notification, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        await scope.ServiceProvider
            .GetRequiredService<IAppLogRepository>()
            .AppendAsync(notification.Entry, ct);
    }
}
