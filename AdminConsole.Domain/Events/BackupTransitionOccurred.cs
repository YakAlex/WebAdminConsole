using AdminConsole.Domain.Models;
using MediatR;

namespace AdminConsole.Domain.Events;

/// <summary>
/// Публікується BackupMonitorService.OnConfirmedTransition ЛИШЕ коли
/// підтверджений перехід у Stale/Missing і НЕ під активним Maintenance-
/// вікном — тобто саме тоді, коли варто розбудити людину.
/// TelegramBotService підписується і розсилає push.
/// Заміна BackupTransitionMessage.
/// </summary>
public sealed record BackupTransitionOccurred(
    string        ServerName,
    BackupKind    Kind,
    BackupOutcome Previous,
    BackupOutcome Current
) : INotification;
