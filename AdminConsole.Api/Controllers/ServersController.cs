using AdminConsole.Domain.Models;
using AdminConsole.Infrastructure.Remote;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace AdminConsole.Api.Controllers;

public sealed record ServerActionResult(bool Success, string? Error);

/// <summary>
/// GET /api/servers — сконфігурований список серверів (Servers у appsettings.json).
///
/// Пріоритет 3, #3.1 (2026-08-22): дії керування (Restart/Shutdown/RDP-файл)
/// додано сюди, а не в окремий контролер — вони діють саме над записами з
/// цього ж списку. Усі три перевіряють ip проти конфігурації (а не довіряють
/// довільному значенню з URL) і Restart/Shutdown додатково вимагають
/// ServerType.Windows — WMI Win32Shutdown для Linux/Network пристроїв не має
/// сенсу. Авторизація — та сама спільна Viewer-політика (рішення користувача:
/// AD-група й так містить лише довірених адмінів, окрему Admin-політику не
/// заводимо).
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
        if (server is null) return NotFound(new { error = $"Сервер з IP '{ip}' не знайдено в конфігурації." });
        if (server.Type != ServerType.Windows)
            return BadRequest(new { error = "Restart підтримується лише для Windows-серверів." });

        var (success, error) = await remoteManagement.RemoteRestartAsync(server.IP, server.Name, ct);
        return Ok(new ServerActionResult(success, error));
    }

    [HttpPost("{ip}/shutdown")]
    public async Task<ActionResult<ServerActionResult>> Shutdown(string ip, CancellationToken ct)
    {
        var server = Find(ip);
        if (server is null) return NotFound(new { error = $"Сервер з IP '{ip}' не знайдено в конфігурації." });
        if (server.Type != ServerType.Windows)
            return BadRequest(new { error = "Shutdown підтримується лише для Windows-серверів." });

        var (success, error) = await remoteManagement.RemoteShutdownAsync(server.IP, server.Name, ct);
        return Ok(new ServerActionResult(success, error));
    }

    /// <summary>
    /// .rdp-файл на скачування — веб-нативна заміна WPF-виклику mstsc.exe
    /// (той спрацьовував лише тому, що WPF крутився на комп'ютері адміна;
    /// headless-служба такого робочого стола не має). Браузер качає файл,
    /// локальний RDP-клієнт адміна відкриває його сам. Свідомо без
    /// "username:s:..." — Windows сам запитає обліковку (prompt for
    /// credentials:i:1), жоден логін не хардкодиться у файл.
    /// </summary>
    [HttpGet("{ip}/rdp-file")]
    public ActionResult DownloadRdpFile(string ip)
    {
        var server = Find(ip);
        if (server is null) return NotFound(new { error = $"Сервер з IP '{ip}' не знайдено в конфігурації." });
        if (server.Type != ServerType.Windows)
            return BadRequest(new { error = "RDP підтримується лише для Windows-серверів." });

        string content = $"full address:s:{server.IP}\r\nprompt for credentials:i:1\r\n";
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(content);

        string safeName = new string(server.Name.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
        if (string.IsNullOrEmpty(safeName)) safeName = "server";

        return File(bytes, "application/x-rdp", $"{safeName}.rdp");
    }
}
