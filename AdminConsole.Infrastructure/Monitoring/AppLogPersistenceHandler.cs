using AdminConsole.Domain.Abstractions;
using AdminConsole.Domain.Events;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
public sealed class AppLogPersistenceHandler(IServiceScopeFactory scopeFactory, ILogger<AppLogPersistenceHandler> logger)
    : INotificationHandler<AppLogEntryOccurred>
{
    public async Task Handle(AppLogEntryOccurred notification, CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            await scope.ServiceProvider
                .GetRequiredService<IAppLogRepository>()
                .AppendAsync(notification.Entry, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // A subscriber must never take down its publisher (see
            // UptimeTrackerService.Handle(PingBatchResultOccurred) for the
            // same class of fix) — a transient SQLite failure here must not
            // propagate back through MediatR's sequential default publisher
            // into whichever service raised this log entry in the first
            // place. Logged via ILogger, not another AppLogEntryOccurred —
            // this handler IS the thing that persists those.
            logger.LogError(ex, "AppLogPersistenceHandler: failed to persist a log entry.");
        }
    }
}
