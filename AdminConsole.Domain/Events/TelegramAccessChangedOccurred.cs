using MediatR;

namespace AdminConsole.Domain.Events;

public enum TelegramAccessAction { Approved, Denied, Revoked }

/// <summary>
/// Published on approve/deny/revoke (from either channel — a Telegram
/// button or React Settings) — keeps the pending/users list in sync across
/// both UIs at once.
/// Replaces TelegramAccessChangedMessage.
/// </summary>
public sealed record TelegramAccessChangedOccurred(
    TelegramAccessAction Action,
    long                 ChatId,
    string?              Username
) : INotification;
