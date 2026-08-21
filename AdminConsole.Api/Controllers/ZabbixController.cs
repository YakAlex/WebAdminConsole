using AdminConsole.Domain.Events;
using AdminConsole.Infrastructure.Zabbix;
using Microsoft.AspNetCore.Mvc;

namespace AdminConsole.Api.Controllers;

/// <summary>
/// GET /api/zabbix — живий знімок активних Zabbix-проблем ЗАРАЗ (реально
/// опитує Zabbix API в момент запиту, не кеш) — для початкового завантаження
/// вкладки Zabbix Alerts. Той самий принцип, що й PingController/PingMonitorService
/// (Фаза 10, Крок 11.1 аудиту — раніше сторінка мала лише SignalR-потік, без
/// REST-знімка, тож при заході/F5 показувала нуль даних до наступного циклу
/// поллінгу).
/// </summary>
public sealed class ZabbixController(ZabbixPollerService pollerService) : AdminConsoleControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ZabbixProblemsPayload>> Get(CancellationToken ct) =>
        Ok(await pollerService.GetActiveProblemsNowAsync(ct));
}
