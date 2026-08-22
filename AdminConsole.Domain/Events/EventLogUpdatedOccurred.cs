using AdminConsole.Domain.Models;
using MediatR;

namespace AdminConsole.Domain.Events;

/// <summary>
/// Published by EventLogService after every read of new entries.
/// Carries a full replacement list — the subscriber clears and repopulates.
/// Replaces EventLogUpdatedMessage.
/// </summary>
public sealed record EventLogUpdatedOccurred(
    IReadOnlyList<EventLogEntry> Entries
) : INotification;
