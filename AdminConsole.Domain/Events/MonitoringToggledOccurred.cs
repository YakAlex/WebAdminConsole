using MediatR;

namespace AdminConsole.Domain.Events;

/// <summary>Which background monitoring service the Settings toggle affects.</summary>
public enum MonitoredService
{
    Rdp,
    Zabbix,
    Backups
}

/// <summary>
/// Published when the user flips a monitoring toggle in Settings — a
/// "wake up and check" signal for RdpMonitorService/ZabbixPollerService.
///
/// IMPORTANT: the background services use receipt of this event only as a
/// trigger — they do NOT trust the Enabled field carried by the event
/// itself, and always re-read the source of truth (the settings
/// repository) themselves (Pull). This eliminates the risk of the event's
/// payload drifting out of sync with what's actually in the configuration.
/// Replaces MonitoringToggledMessage.
/// </summary>
public sealed record MonitoringToggledOccurred(
    MonitoredService Service,
    bool             Enabled
) : INotification;
