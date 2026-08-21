using AdminConsole.Domain.Models;
using MediatR;

namespace AdminConsole.Domain.Events;

/// <summary>
/// Публікується PingMonitorService раз на повний цикл опитування.
/// Містить результати ВСІХ серверів — одна подія на цикл замість
/// однієї на сервер. Заміна PingBatchResultMessage.
/// </summary>
public sealed record PingBatchResultOccurred(PingBatchPayload Payload) : INotification;

public sealed record PingBatchPayload(
    IReadOnlyList<PingResult> Results,
    DateTimeOffset            CycleCompletedAt
);
