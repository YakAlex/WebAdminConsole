using AdminConsole.Domain.Models;
using MediatR;

namespace AdminConsole.Domain.Events;

/// <summary>
/// Публікується ResourceMonitorService на кожному циклі опитування.
/// Заміна ResourceSnapshotUpdatedMessage.
/// </summary>
public sealed record ResourceSnapshotUpdatedOccurred(
    ResourceSnapshot Snapshot
) : INotification;
