using AdminConsole.Api.Hubs;
using AdminConsole.Domain.Events;
using MediatR;
using Microsoft.AspNetCore.SignalR;

namespace AdminConsole.Api.Realtime;

/// <summary>
/// Міст доменних подій (MediatR INotification, Domain/Events, Фаза 1) →
/// SignalR DashboardHub. Замінює пряму WPF-підписку ViewModel-ів на
/// IMessenger — кожна доменна подія автоматично летить у відповідну групу
/// замість прямого виклику підписника.
///
/// Обробляє ВСІ 14 подій, перенесених з Core/Messages. Чотири з них мають
/// прямий, однозначний UI-відповідник (ping/uptime/backups); решта — RDP,
/// Zabbix, Resource Monitor, Event Log, credentials/monitoring-toggle,
/// Telegram access — не мають власної сторінки в поточному обсязі Фази 6
/// (Dashboard/Uptime/Backups/Logs/Maintenance/Settings), тож летять у
/// "logs" як загальний потік адміністративної активності — той самий
/// принцип, що й у старому WPF, де все зрештою потрапляло у вкладку Logs
/// через AppLogEntryMessage.
/// </summary>
public sealed class SignalRBroadcastHandler(IHubContext<DashboardHub> hub) :
    INotificationHandler<AppLogEntryOccurred>,
    INotificationHandler<PingBatchResultOccurred>,
    INotificationHandler<MaintenanceChangedOccurred>,
    INotificationHandler<BackupStatusUpdatedOccurred>,
    INotificationHandler<BackupTransitionOccurred>,
    INotificationHandler<UptimeUpdatedOccurred>,
    INotificationHandler<EventLogUpdatedOccurred>,
    INotificationHandler<ResourceSnapshotUpdatedOccurred>,
    INotificationHandler<RdpSessionsUpdatedOccurred>,
    INotificationHandler<ZabbixProblemsUpdatedOccurred>,
    INotificationHandler<CredentialsChangedOccurred>,
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

    // Впливає і на бейдж Ping Dashboard, і на трекінг інцидентів Uptime —
    // той самий подвійний вплив, що мав MaintenanceChangedMessage у WPF
    // (підписники: UptimeTrackerService, PingMonitorService, PingResultViewModel).
    public Task Handle(MaintenanceChangedOccurred n, CancellationToken ct) =>
        Task.WhenAll(Send(Ping, n, ct), Send(Uptime, n, ct));

    public Task Handle(BackupStatusUpdatedOccurred n, CancellationToken ct) => Send(Backups, n, ct);

    public Task Handle(BackupTransitionOccurred n, CancellationToken ct) => Send(Backups, n, ct);

    public Task Handle(UptimeUpdatedOccurred n, CancellationToken ct) => Send(Uptime, n, ct);

    public Task Handle(EventLogUpdatedOccurred n, CancellationToken ct) => Send(Logs, n, ct);

    public Task Handle(ResourceSnapshotUpdatedOccurred n, CancellationToken ct) => Send(Logs, n, ct);

    public Task Handle(RdpSessionsUpdatedOccurred n, CancellationToken ct) => Send(Logs, n, ct);

    public Task Handle(ZabbixProblemsUpdatedOccurred n, CancellationToken ct) => Send(Logs, n, ct);

    public Task Handle(CredentialsChangedOccurred n, CancellationToken ct) => Send(Logs, n, ct);

    public Task Handle(MonitoringToggledOccurred n, CancellationToken ct) => Send(Logs, n, ct);

    public Task Handle(TelegramAccessChangedOccurred n, CancellationToken ct) => Send(Logs, n, ct);

    public Task Handle(TelegramAccessRequestOccurred n, CancellationToken ct) => Send(Logs, n, ct);

    private Task Send<T>(string group, T notification, CancellationToken ct) where T : notnull =>
        hub.Clients.Group(group).SendAsync(typeof(T).Name, notification, ct);
}
