using AdminConsole.Domain.Models;
using MediatR;

namespace AdminConsole.Domain.Events;

/// <summary>
/// Публікується коли прийшов новий /start від неавторизованого chat_id.
/// SignalR-хендлер розсилає pending-запит у React Settings як backup-канал
/// (на випадок якщо Primary Admin не в мережі в Telegram, але дивиться в дашборд).
/// Заміна TelegramAccessRequestMessage.
/// </summary>
public sealed record TelegramAccessRequestOccurred(
    TelegramPendingRequest Request
) : INotification;
