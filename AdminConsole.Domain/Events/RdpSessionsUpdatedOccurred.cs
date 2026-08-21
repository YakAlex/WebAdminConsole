using AdminConsole.Domain.Models;
using MediatR;

namespace AdminConsole.Domain.Events;

/// <summary>
/// Публікується RdpMonitorService після кожного опитування quser для сервера.
/// Несе повний список заміни сесій для цього сервера.
/// Заміна RdpSessionsUpdatedMessage.
/// </summary>
public sealed record RdpSessionsUpdatedOccurred(RdpSessionsPayload Payload) : INotification;

public sealed record RdpSessionsPayload(
    string                         ServerName,
    string                         ServerIp,
    IReadOnlyList<RdpSessionInfo>  Sessions,
    string?                        ErrorMessage,
    int                            GlobalDailyPeak,
    string?                        LastLogoutUsername,
    string?                        LastLogoutServer,
    DateTimeOffset?                LastLogoutAt
);
