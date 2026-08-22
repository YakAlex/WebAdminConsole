using AdminConsole.Domain.Models;
using MediatR;

namespace AdminConsole.Domain.Events;

/// <summary>
/// Published by UptimeTrackerService whenever the incident list changes.
/// Replaces UptimeUpdatedMessage.
/// </summary>
public sealed record UptimeUpdatedOccurred(
    IReadOnlyList<DowntimeRecord> Snapshot
) : INotification;
