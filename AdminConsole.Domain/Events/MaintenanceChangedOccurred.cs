using AdminConsole.Domain.Models;
using MediatR;

namespace AdminConsole.Domain.Events;

public enum MaintenanceAction { Started, Ended }

/// <summary>
/// Published by MaintenanceService when a window starts/ends
/// (either manually via the UI, or automatically when To elapses).
///
/// Subscribers:
///   - UptimeTrackerService  (Started) — closes "stuck" open incidents
///   - PingMonitorService    (Ended)   — resets previousStatus so a fresh
///                                       alert fires if the server is still Offline
///   - SignalR handler       (both)    — instantly updates the badge in the React UI
///
/// Replaces MaintenanceChangedMessage.
/// </summary>
public sealed record MaintenanceChangedOccurred(
    MaintenanceAction Action,
    MaintenanceWindow Window
) : INotification;
