using AdminConsole.Domain.Abstractions;
using AdminConsole.Domain.Events;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace AdminConsole.Infrastructure.Monitoring;

/// <summary>
/// T4.13: replacement for FileLoggerService (removed entirely) — instead
/// of a ConcurrentQueue + a separate flush cycle into a rolling
/// app-YYYY-MM-DD.log, every AppLogEntryOccurred is written immediately to
/// the AppLogEntries table via IAppLogRepository. This eliminates the
/// entire class of multi-file merge / "tail of the newest file at startup"
/// problems — it's now just an ORDER BY Timestamp DESC LIMIT :take in SQL
/// (LogsController, Phase 3).
///
/// MediatR fan-out means this handler runs in parallel with
/// SignalRBroadcastHandler (Api, Phase 3) for the SAME event — one failing
/// doesn't take down the other.
///
/// IServiceScopeFactory instead of injecting IAppLogRepository directly:
/// this handler is registered as Singleton (the same root provider as the
/// rest of the Infrastructure handlers — MediatR, injected into a
/// Singleton service like UptimeTrackerService, calls Publish through the
/// CAPTURED root provider, so NO handler it resolves can be Scoped — even
/// if the handler itself isn't syntactically Singleton).
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
