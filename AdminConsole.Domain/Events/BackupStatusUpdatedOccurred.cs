using AdminConsole.Domain.Models;
using MediatR;

namespace AdminConsole.Domain.Events;

/// <summary>
/// Published by BackupMonitorService once per cycle (after SaveToDb),
/// with a snapshot of all BackupCheckState. Replaces BackupStatusUpdatedMessage.
///
/// A snapshot (not the original mutable objects) — the same principle
/// already applied for DowntimeRecord/UptimeTrackerService (CloneRecord),
/// to avoid a data race between the background cycle and readers.
/// </summary>
public sealed record BackupStatusUpdatedOccurred(
    IReadOnlyList<BackupCheckState> Snapshot
) : INotification;
