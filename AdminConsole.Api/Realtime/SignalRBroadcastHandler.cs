using AdminConsole.Api.Hubs;
using AdminConsole.Domain.Events;
using MediatR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace AdminConsole.Api.Realtime;

/// <summary>
/// Bridge from domain events (MediatR INotification, Domain/Events, Phase 1) →
/// SignalR DashboardHub. Replaces the old WPF direct ViewModel subscription via
/// IMessenger — every domain event now automatically flows into the matching
/// group instead of directly invoking a subscriber.
///
/// Handles events carried over from Core/Messages. Four of them have a
/// direct, unambiguous UI counterpart (ping/uptime/backups); the rest — RDP,
/// Zabbix, monitoring-toggle, Telegram access — don't
/// have their own page in the current Phase 6 scope
/// (Dashboard/Uptime/Backups/Logs/Maintenance/Settings), so they go into
/// "logs" as a general stream of administrative activity — the same
/// principle used in the old WPF app, where everything eventually ended up
/// in the Logs tab via AppLogEntryMessage.
///
/// (ResourceSnapshotUpdatedOccurred/ResourceMonitorService was removed
/// entirely on 2026-08-22 along with the frontend Resources tab;
/// EventLogUpdatedOccurred/EventLogService removed 2026-08-23, audit
/// Finding 3.1 — a fully-built feature with zero consumers anywhere.
/// CredentialsChangedOccurred/BackupTransitionOccurred broadcast legs
/// removed 2026-08-23, dead-code audit — no frontend ever subscribed to
/// either by name; the underlying MediatR events are still published and
/// still have real backend subscribers — ZabbixPollerService's wake-up on
/// credential save, TelegramBotService's backup-transition alerts — this
/// class was just never one of their consumers.)
/// </summary>
public sealed class SignalRBroadcastHandler(IHubContext<DashboardHub> hub, ILogger<SignalRBroadcastHandler> logger) :
    INotificationHandler<AppLogEntryOccurred>,
    INotificationHandler<PingBatchResultOccurred>,
    INotificationHandler<MaintenanceChangedOccurred>,
    INotificationHandler<BackupStatusUpdatedOccurred>,
    INotificationHandler<UptimeUpdatedOccurred>,
    INotificationHandler<RdpSessionsUpdatedOccurred>,
    INotificationHandler<ZabbixProblemsUpdatedOccurred>,
    INotificationHandler<MonitoringToggledOccurred>,
    INotificationHandler<TelegramAccessChangedOccurred>,
    INotificationHandler<TelegramAccessRequestOccurred>
{
    private const string Ping    = "ping";
    private const string Uptime  = "uptime";
    private const string Backups = "backups";
    private const string Logs    = "logs";

    public Task Handle(AppLogEntryOccurred n, CancellationToken ct) => Send(Logs, n, ct);

    public Task Handle(PingBatchResultOccurred n, CancellationToken ct) => Send(Ping, n, ct);

    // Affects both the Ping Dashboard badge and the Uptime incident tracking —
    // the same dual effect MaintenanceChangedMessage had in WPF (subscribers:
    // UptimeTrackerService, PingMonitorService, PingResultViewModel).
    public Task Handle(MaintenanceChangedOccurred n, CancellationToken ct) =>
        Task.WhenAll(Send(Ping, n, ct), Send(Uptime, n, ct));

    public Task Handle(BackupStatusUpdatedOccurred n, CancellationToken ct) => Send(Backups, n, ct);

    public Task Handle(UptimeUpdatedOccurred n, CancellationToken ct) => Send(Uptime, n, ct);

    public Task Handle(RdpSessionsUpdatedOccurred n, CancellationToken ct) => Send(Logs, n, ct);

    public Task Handle(ZabbixProblemsUpdatedOccurred n, CancellationToken ct) => Send(Logs, n, ct);

    public Task Handle(MonitoringToggledOccurred n, CancellationToken ct) => Send(Logs, n, ct);

    public Task Handle(TelegramAccessChangedOccurred n, CancellationToken ct) => Send(Logs, n, ct);

    public Task Handle(TelegramAccessRequestOccurred n, CancellationToken ct) => Send(Logs, n, ct);

    // Audit Zone 5, Finding #3 (2026-08-22): several event types have
    // MULTIPLE MediatR handlers registered at once (e.g. AppLogEntryOccurred
    // → both AppLogPersistenceHandler and this one). By default MediatR runs
    // them sequentially and stops at the first exception — a SignalR
    // transport failure (client disconnected right at broadcast time, a
    // perfectly normal and frequent occurrence) could silently swallow the
    // neighboring handler (writing to AppLogEntries or sending a Telegram
    // notification), and thanks to the Zone 1 guard, also prematurely end the
    // caller's current poll cycle. A SignalR failure on its own should never
    // have affected anything beyond the broadcast itself.
    private async Task Send<T>(string group, T notification, CancellationToken ct) where T : notnull
    {
        try
        {
            await hub.Clients.Group(group).SendAsync(typeof(T).Name, notification, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "SignalRBroadcastHandler: failed to broadcast {EventType} to group {Group}.",
                typeof(T).Name, group);
        }
    }
}
