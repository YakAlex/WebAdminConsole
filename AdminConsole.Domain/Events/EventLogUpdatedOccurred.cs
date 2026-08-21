using AdminConsole.Domain.Models;
using MediatR;

namespace AdminConsole.Domain.Events;

/// <summary>
/// Публікується EventLogService після кожного зчитування нових записів.
/// Несе повний список заміни — підписник очищає і наповнює наново.
/// Заміна EventLogUpdatedMessage.
/// </summary>
public sealed record EventLogUpdatedOccurred(
    IReadOnlyList<EventLogEntry> Entries
) : INotification;
