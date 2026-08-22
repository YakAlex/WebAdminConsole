using AdminConsole.Domain.Events;
using AdminConsole.Infrastructure.Zabbix;
using Microsoft.AspNetCore.Mvc;

namespace AdminConsole.Api.Controllers;

/// <summary>
/// GET /api/zabbix — a live snapshot of active Zabbix problems RIGHT NOW
/// (actually queries the Zabbix API at request time, not a cache) — for the
/// initial load of the Zabbix Alerts tab. The same principle as
/// PingController/PingMonitorService (Phase 10, audit step 11.1 — previously
/// the page only had a SignalR stream, no REST snapshot, so on
/// navigation/F5 it showed zero data until the next poll cycle).
/// </summary>
public sealed class ZabbixController(ZabbixPollerService pollerService) : AdminConsoleControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ZabbixProblemsPayload>> Get(CancellationToken ct) =>
        Ok(await pollerService.GetActiveProblemsNowAsync(ct));
}
