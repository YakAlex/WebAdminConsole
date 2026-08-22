using AdminConsole.Domain.Events;
using AdminConsole.Infrastructure.Monitoring;
using Microsoft.AspNetCore.Mvc;

namespace AdminConsole.Api.Controllers;

/// <summary>
/// GET /api/ping — an initial live snapshot of ping statuses for pages that
/// mount AFTER the service has started (Overview/Ping). Previously the only
/// source was SignalR's PingBatchResultOccurred, so cards stayed empty until
/// the next poll cycle (up to PingIntervalSeconds, 30s by default) — the
/// frontend had no way to get the "last known state" on mount.
///
/// Reuses PingMonitorService.PingAllNowAsync — the same method already used
/// by the Telegram bot's /ping command (Phase 5): it actually pings all
/// servers RIGHT NOW and publishes PingBatchResultOccurred (other connected
/// clients also see the update via SignalR), rather than just returning a
/// stale cache from the previous cycle.
/// </summary>
public sealed class PingController(PingMonitorService pingMonitor) : AdminConsoleControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PingBatchPayload>> Get(CancellationToken ct)
    {
        var results = await pingMonitor.PingAllNowAsync(ct);
        return Ok(new PingBatchPayload(results, DateTimeOffset.Now));
    }
}
