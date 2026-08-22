using AdminConsole.Domain.Models;
using MediatR;

namespace AdminConsole.Domain.Events;

/// <summary>
/// Published by ZabbixPollerService after every successful or failed poll.
/// On failure, Problems is empty and ErrorMessage is populated.
/// Replaces ZabbixProblemsUpdatedMessage.
/// </summary>
public sealed record ZabbixProblemsUpdatedOccurred(ZabbixProblemsPayload Payload) : INotification;

public sealed record ZabbixProblemsPayload(
    IReadOnlyList<ZabbixProblem>? Problems,
    string?                       ErrorMessage,
    DateTimeOffset                FetchedAt
);
