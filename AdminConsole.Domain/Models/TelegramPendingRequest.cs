namespace AdminConsole.Domain.Models;

/// <summary>
/// A request for Telegram bot access from a new user (/start).
/// Lives purely in memory (TelegramAccessControlService) — not persisted,
/// since a request lost on restart will simply be resent by the user.
/// </summary>
public sealed record TelegramPendingRequest(
    int            Id,
    long           ChatId,
    string         Username,
    DateTimeOffset RequestedAt);
