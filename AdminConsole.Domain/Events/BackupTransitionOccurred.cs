using AdminConsole.Domain.Models;
using MediatR;

namespace AdminConsole.Domain.Events;

/// <summary>
/// Published by BackupMonitorService.OnConfirmedTransition ONLY on a
/// confirmed transition to Stale/Missing that is NOT under an active
/// Maintenance window — i.e. exactly when it's worth waking someone up.
/// TelegramBotService subscribes and sends a push notification.
/// Replaces BackupTransitionMessage.
/// </summary>
public sealed record BackupTransitionOccurred(
    string        ServerName,
    BackupKind    Kind,
    BackupOutcome Previous,
    BackupOutcome Current
) : INotification;
