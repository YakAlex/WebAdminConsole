using AdminConsole.Domain.Models;
using MediatR;

namespace AdminConsole.Domain.Events;

/// <summary>
/// Published when a new /start arrives from an unauthorized chat_id.
/// The SignalR handler broadcasts the pending request to React Settings as
/// a backup channel (in case the Primary Admin isn't online in Telegram but
/// is watching the dashboard).
/// Replaces TelegramAccessRequestMessage.
/// </summary>
public sealed record TelegramAccessRequestOccurred(
    TelegramPendingRequest Request
) : INotification;
