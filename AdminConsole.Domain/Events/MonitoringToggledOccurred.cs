using MediatR;

namespace AdminConsole.Domain.Events;

/// <summary>Який фоновий сервіс моніторингу зачіпає перемикач у Settings.</summary>
public enum MonitoredService
{
    Rdp,
    Zabbix,
    Backups
}

/// <summary>
/// Публікується коли користувач перемикає тумблер моніторингу в Settings —
/// сигнал "прокинься і перевір" для RdpMonitorService/ZabbixPollerService.
///
/// ВАЖЛИВО: фонові сервіси використовують факт отримання цієї події лише
/// як тригер — вони НЕ довіряють полю Enabled з самої події, а завжди
/// перечитують джерело істини (репозиторій налаштувань) самостійно (Pull).
/// Це усуває ризик розсинхронізації між тим, що написано в події,
/// і тим, що зараз реально в конфігурації.
/// Заміна MonitoringToggledMessage.
/// </summary>
public sealed record MonitoringToggledOccurred(
    MonitoredService Service,
    bool             Enabled
) : INotification;
