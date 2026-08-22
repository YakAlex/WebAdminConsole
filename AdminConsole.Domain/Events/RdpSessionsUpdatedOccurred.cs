using AdminConsole.Domain.Models;
using MediatR;

namespace AdminConsole.Domain.Events;

/// <summary>
/// Published by RdpMonitorService after every quser poll for a server.
/// Carries the full replacement session list for that server.
/// Replaces RdpSessionsUpdatedMessage.
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
