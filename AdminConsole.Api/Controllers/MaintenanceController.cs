using AdminConsole.Domain.Models;
using AdminConsole.Infrastructure.Monitoring;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace AdminConsole.Api.Controllers;

public sealed record StartMaintenanceRequest(string? ServerIp, string? TargetGroup, int? DurationMinutes, string? Reason);

/// <summary>
/// Audit fix (2026-08-22, item 1 of the migration gap report): a REST layer
/// over MaintenanceService.StartMaintenanceAsync/EndMaintenanceEarlyAsync —
/// both had existed since Phase 4 in anticipation of a "future Settings/
/// Maintenance API" (see the class comment), nobody had just called them
/// from REST yet.
///
/// GET also closes a second, related gap: useMaintenanceWindows() on the
/// frontend was a pure SignalR stream (only MaintenanceChangedOccurred on
/// each Start/End) — windows that were already active before someone opened
/// the page were invisible until the next event. The same class of bug
/// already fixed for the Zabbix/RDP/Ping REST snapshots.
/// </summary>
public sealed class MaintenanceController(MaintenanceService maintenance, IOptions<List<ServerEntry>> servers)
    : AdminConsoleControllerBase
{
    [HttpGet]
    public ActionResult<IReadOnlyList<MaintenanceWindow>> Get() => Ok(maintenance.GetActiveWindows());

    /// <summary>
    /// Exactly one of ServerIp/TargetGroup — both are validated against the
    /// real appsettings.json list (the same protection against arbitrary
    /// client input already used in ServersController.Find) — DisplayName is
    /// built here, not sent by the client. To is computed on the backend from
    /// DateTimeOffset.Now rather than accepted as a ready-made timestamp from
    /// the frontend.
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<MaintenanceWindow>> Start([FromBody] StartMaintenanceRequest request, CancellationToken ct)
    {
        bool hasServer = !string.IsNullOrWhiteSpace(request.ServerIp);
        bool hasGroup = !string.IsNullOrWhiteSpace(request.TargetGroup);

        if (hasServer == hasGroup)
            return BadRequest(new { error = "Specify either serverIp or targetGroup — exactly one of the two." });

        if (request.DurationMinutes is { } minutes && minutes <= 0)
            return BadRequest(new { error = "durationMinutes must be a positive number, or omitted (no time limit)." });

        string displayName;
        string? serverIp = null;
        string? targetGroup = null;

        if (hasServer)
        {
            var server = servers.Value.FirstOrDefault(s => s.IP == request.ServerIp);
            if (server is null)
                return NotFound(new { error = $"Server with IP '{request.ServerIp}' not found in configuration." });

            serverIp = server.IP;
            displayName = server.Name;
        }
        else
        {
            var group = servers.Value.FirstOrDefault(s =>
                s.Group.Equals(request.TargetGroup, StringComparison.OrdinalIgnoreCase))?.Group;
            if (group is null)
                return NotFound(new { error = $"Group '{request.TargetGroup}' not found in configuration." });

            targetGroup = group;
            displayName = group;
        }

        var window = new MaintenanceWindow
        {
            ServerIp = serverIp,
            TargetGroup = targetGroup,
            DisplayName = displayName,
            From = DateTimeOffset.Now,
            To = request.DurationMinutes is { } m ? DateTimeOffset.Now.AddMinutes(m) : null,
            Reason = request.Reason?.Trim() ?? string.Empty,
        };

        await maintenance.StartMaintenanceAsync(window, ct);
        return Ok(window);
    }

    /// <summary>Key — ServerIp or "group:{TargetGroup}" (MaintenanceWindow.Key), passed as a query parameter — avoids ':' issues in the route.</summary>
    [HttpDelete]
    public async Task<IActionResult> End([FromQuery] string key, CancellationToken ct)
    {
        bool ended = await maintenance.EndMaintenanceEarlyAsync(key, ct);
        return ended ? NoContent() : NotFound(new { error = $"No active maintenance window found with key '{key}'." });
    }
}
