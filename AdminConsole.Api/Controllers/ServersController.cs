using AdminConsole.Domain.Models;
using AdminConsole.Infrastructure.Remote;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace AdminConsole.Api.Controllers;

public sealed record ServerActionResult(bool Success, string? Error);

/// <summary>
/// GET /api/servers — the configured list of servers (Servers in appsettings.json).
///
/// Priority 3, #3.1 (2026-08-22): management actions (Restart/Shutdown) were
/// added here rather than in a separate controller — they act specifically
/// on entries from this same list. Both validate the ip against the
/// configuration (rather than trusting an arbitrary value from the URL),
/// and additionally require ServerType.Windows —
/// WMI Win32Shutdown makes no sense for Linux/Network devices. Authorization
/// is the same shared Viewer policy (user's decision: the AD group already
/// contains only trusted admins, so a separate Admin policy isn't needed).
/// </summary>
public sealed class ServersController(IOptions<List<ServerEntry>> servers, RemoteManagementService remoteManagement)
    : AdminConsoleControllerBase
{
    [HttpGet]
    public ActionResult<IReadOnlyList<ServerEntry>> Get() => Ok(servers.Value);

    private ServerEntry? Find(string ip) => servers.Value.FirstOrDefault(s => s.IP == ip);

    [HttpPost("{ip}/restart")]
    public async Task<ActionResult<ServerActionResult>> Restart(string ip, CancellationToken ct)
    {
        var server = Find(ip);
        if (server is null) return NotFound(new { error = $"Server with IP '{ip}' not found in configuration." });
        if (server.Type != ServerType.Windows)
            return BadRequest(new { error = "Restart is only supported for Windows servers." });

        var (success, error) = await remoteManagement.RemoteRestartAsync(server.IP, server.Name, ct);
        return Ok(new ServerActionResult(success, error));
    }

    [HttpPost("{ip}/shutdown")]
    public async Task<ActionResult<ServerActionResult>> Shutdown(string ip, CancellationToken ct)
    {
        var server = Find(ip);
        if (server is null) return NotFound(new { error = $"Server with IP '{ip}' not found in configuration." });
        if (server.Type != ServerType.Windows)
            return BadRequest(new { error = "Shutdown is only supported for Windows servers." });

        var (success, error) = await remoteManagement.RemoteShutdownAsync(server.IP, server.Name, ct);
        return Ok(new ServerActionResult(success, error));
    }
}
