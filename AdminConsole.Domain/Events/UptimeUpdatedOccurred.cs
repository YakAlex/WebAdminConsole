using AdminConsole.Domain.Models;
using MediatR;

namespace AdminConsole.Domain.Events;

/// <summary>
/// Публікується UptimeTrackerService при кожній зміні списку інцидентів.
/// Заміна UptimeUpdatedMessage.
/// </summary>
public sealed record UptimeUpdatedOccurred(
    IReadOnlyList<DowntimeRecord> Snapshot
) : INotification;
