using AdminConsole.Domain.Models;
using AdminConsole.Infrastructure.Remote;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace AdminConsole.Api.Controllers;

public sealed record ServerActionResult(bool Success, string? Error);

/// <summary>
/// GET /api/servers — the configured list of servers (Servers in appsettings.json).
///
/// Priority 3, #3.1 (2026-08-22): management actions (Restart/Shutdown/RDP
/// file) were added here rather than in a separate controller — they act
/// specifically on entries from this same list. All three validate the ip
/// against the configuration (rather than trusting an arbitrary value from
/// the URL), and Restart/Shutdown additionally require ServerType.Windows —
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

    /// <summary>
    /// A downloadable .rdp file — the web-native replacement for the WPF call
    /// to mstsc.exe (that only worked because WPF ran on the admin's own
    /// machine; a headless service has no desktop of its own). The browser
    /// downloads the file, and the admin's local RDP client opens it. Deliberately
    /// without "username:s:..." — Windows itself will prompt for credentials
    /// (prompt for credentials:i:1), no login is hardcoded into the file.
    /// </summary>
    [HttpGet("{ip}/rdp-file")]
    public ActionResult DownloadRdpFile(string ip)
    {
        var server = Find(ip);
        if (server is null) return NotFound(new { error = $"Server with IP '{ip}' not found in configuration." });
        if (server.Type != ServerType.Windows)
            return BadRequest(new { error = "RDP is only supported for Windows servers." });

        string content = $"full address:s:{server.IP}\r\nprompt for credentials:i:1\r\n";
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(content);

        string safeName = new string(server.Name.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
        if (string.IsNullOrEmpty(safeName)) safeName = "server";

        return File(bytes, "application/x-rdp", $"{safeName}.rdp");
    }
}
