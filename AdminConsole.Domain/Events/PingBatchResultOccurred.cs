using AdminConsole.Domain.Models;
using MediatR;

namespace AdminConsole.Domain.Events;

/// <summary>
/// Published by PingMonitorService once per full polling cycle.
/// Contains results for ALL servers — one event per cycle instead of
/// one per server. Replaces PingBatchResultMessage.
/// </summary>
public sealed record PingBatchResultOccurred(PingBatchPayload Payload) : INotification;

public sealed record PingBatchPayload(
    IReadOnlyList<PingResult> Results,
    DateTimeOffset            CycleCompletedAt
);
