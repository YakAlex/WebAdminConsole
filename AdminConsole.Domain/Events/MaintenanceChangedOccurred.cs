using AdminConsole.Domain.Models;
using MediatR;

namespace AdminConsole.Domain.Events;

public enum MaintenanceAction { Started, Ended }

/// <summary>
/// Публікується MaintenanceService при старті/завершенні вікна
/// (як вручну через UI, так і автоматично по закінченню To).
///
/// Підписники:
///   - UptimeTrackerService  (Started) — закриває "завислі" відкриті інциденти
///   - PingMonitorService    (Ended)   — скидає previousStatus щоб згенерувати
///                                       свіжий алерт якщо сервер все ще Offline
///   - SignalR-хендлер       (обидва)  — миттєво оновлює бейдж у React UI
///
/// Заміна MaintenanceChangedMessage.
/// </summary>
public sealed record MaintenanceChangedOccurred(
    MaintenanceAction Action,
    MaintenanceWindow Window
) : INotification;
