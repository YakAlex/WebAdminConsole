using MediatR;

namespace AdminConsole.Domain.Events;

public enum TelegramAccessAction { Approved, Denied, Revoked }

/// <summary>
/// Публікується при approve/deny/revoke (з будь-якого каналу — Telegram-кнопка
/// чи React Settings) — синхронізує список pending/users в обох UI одночасно.
/// Заміна TelegramAccessChangedMessage.
/// </summary>
public sealed record TelegramAccessChangedOccurred(
    TelegramAccessAction Action,
    long                 ChatId,
    string?              Username
) : INotification;
