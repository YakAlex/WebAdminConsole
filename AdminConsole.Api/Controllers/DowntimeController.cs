using AdminConsole.Domain.Abstractions;
using AdminConsole.Domain.Events;
using AdminConsole.Domain.Models;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace AdminConsole.Api.Controllers;

/// <summary>
/// GET /api/downtime — all DowntimeRecord entries (initial load for the
/// Uptime tab). DELETE endpoints — Step 5 (#3): removing resolved incidents
/// from the list (equivalent to WPF's UptimeViewModel.DeleteRecord/
/// ClearAllResolved). Both publish UptimeUpdatedOccurred (full snapshot) —
/// the same channel UptimeTrackerService already uses to push live updates,
/// so the requesting client sees the change via SignalR without a separate
/// refetch.
/// </summary>
public sealed class DowntimeController(IDowntimeRepository repository, IMediator mediator) : AdminConsoleControllerBase
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

        await repository.DeleteAsync(serverIp, fellAt, ct);
        await mediator.Publish(new UptimeUpdatedOccurred(await repository.LoadAllAsync(ct)), ct);
        return NoContent();
    }

    /// <summary>Bulk-deletes all resolved incidents (WPF's "Clear History").</summary>
    [HttpDelete("resolved")]
    public async Task<ActionResult<int>> DeleteAllResolved(CancellationToken ct)
    {
        int removed = await repository.DeleteAllResolvedAsync(ct);
        await mediator.Publish(new UptimeUpdatedOccurred(await repository.LoadAllAsync(ct)), ct);
        return Ok(removed);
    }
}
