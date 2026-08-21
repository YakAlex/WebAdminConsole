using AdminConsole.Domain.Models;
using MediatR;

namespace AdminConsole.Domain.Events;

/// <summary>
/// Публікується ZabbixPollerService після кожного успішного чи невдалого
/// опитування. При невдачі Problems порожній, а ErrorMessage заповнений.
/// Заміна ZabbixProblemsUpdatedMessage.
/// </summary>
public sealed record ZabbixProblemsUpdatedOccurred(ZabbixProblemsPayload Payload) : INotification;

public sealed record ZabbixProblemsPayload(
    IReadOnlyList<ZabbixProblem>? Problems,
    string?                       ErrorMessage,
    DateTimeOffset                FetchedAt
);
