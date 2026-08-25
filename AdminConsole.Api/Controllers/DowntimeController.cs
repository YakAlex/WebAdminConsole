using AdminConsole.Domain.Abstractions;
using AdminConsole.Domain.Models;
using AdminConsole.Infrastructure.Monitoring;
using Microsoft.AspNetCore.Mvc;

namespace AdminConsole.Api.Controllers;

/// <summary>
/// GET /api/downtime — all DowntimeRecord entries (initial load for the
/// Uptime tab). DELETE endpoints — Step 5 (#3): removing resolved incidents
/// from the list (equivalent to WPF's UptimeViewModel.DeleteRecord/
/// ClearAllResolved). Both go through UptimeTrackerService.DeleteRecordAsync/
/// ClearAllResolvedAsync — not IDowntimeRepository directly — so the
/// service's in-memory _records cache (what every subsequent snapshot and
/// SLA report actually reads from) stays in sync with the DB; those methods
/// already publish UptimeUpdatedOccurred themselves.
/// </summary>
public sealed class DowntimeController(IDowntimeRepository repository, UptimeTrackerService uptimeTracker) : AdminConsoleControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<DowntimeRecord>>> Get(CancellationToken ct) =>
        Ok(await repository.LoadAllAsync(ct));

    /// <summary>Deletes a single record by its natural key (ServerIp, FellAt) — only resolved incidents can be deleted from the UI.</summary>
    [HttpDelete]
    public async Task<IActionResult> Delete(
        [FromQuery] string serverIp, [FromQuery] DateTimeOffset fellAt, CancellationToken ct)
    {
        var all = await repository.LoadAllAsync(ct);
        var target = all.FirstOrDefault(r => r.ServerIp == serverIp && r.FellAt == fellAt);
        if (target is null) return NotFound();
        if (!target.IsResolved) return BadRequest(new { error = "Cannot delete an open incident." });

        await uptimeTracker.DeleteRecordAsync(target, ct);
        return NoContent();
    }

    /// <summary>Bulk-deletes all resolved incidents (WPF's "Clear History").</summary>
    [HttpDelete("resolved")]
    public async Task<ActionResult<int>> DeleteAllResolved(CancellationToken ct) =>
        Ok(await uptimeTracker.ClearAllResolvedAsync(ct));
}
