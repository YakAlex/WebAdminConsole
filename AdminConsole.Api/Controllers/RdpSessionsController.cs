using AdminConsole.Domain.Models;
using AdminConsole.Infrastructure.Remote;
using Microsoft.AspNetCore.Mvc;

namespace AdminConsole.Api.Controllers;

public sealed record RdpSnapshotPayload(
    IReadOnlyList<RdpSessionInfo> Sessions,
    int                           GlobalDailyPeak,
    string?                       LastLogoutUsername,
    string?                       LastLogoutServer,
    DateTimeOffset?               LastLogoutAt);

/// <summary>
/// GET /api/rdp-sessions — a live snapshot of RDP sessions RIGHT NOW
/// (actually polls the terminal servers via quser at request time) — for the
/// initial load of the RDP Sessions page (Phase 10, audit step 11.2).
///
/// Sanity check (2026-08-21): the base [Route("api/[controller]")] from
/// AdminConsoleControllerBase resolves [controller] literally to
/// "RdpSessions" (no hyphen) — the real route was /api/RdpSessions, while
/// the frontend (endpoints.ts) always requested /api/rdp-sessions. That
/// produced a 404 on EVERY request, before even reaching the network layer
/// — the most likely root cause of the "HTTP 0/unknown error" from item 1
/// (a genuine 404 would have been caught as ApiError(404, ...), but this
/// exact path mismatch was confirmed with a live request against the app —
/// fixed with an explicit [Route].
/// </summary>
[Route("api/rdp-sessions")]
public sealed class RdpSessionsController(RdpMonitorService rdpMonitor) : AdminConsoleControllerBase
{
    [HttpGet]
    public async Task<ActionResult<RdpSnapshotPayload>> Get(CancellationToken ct)
    {
        var (sessions, peak, lastUser, lastServer, lastAt) = await rdpMonitor.GetSnapshotNowAsync(ct);
        return Ok(new RdpSnapshotPayload(sessions, peak, lastUser, lastServer, lastAt));
    }
}
