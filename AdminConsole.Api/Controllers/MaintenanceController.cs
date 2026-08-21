using AdminConsole.Domain.Models;
using AdminConsole.Infrastructure.Monitoring;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace AdminConsole.Api.Controllers;

public sealed record StartMaintenanceRequest(string? ServerIp, string? TargetGroup, int? DurationMinutes, string? Reason);

/// <summary>
/// Аудит-фікс (2026-08-22, п.1 звіту про прогалини міграції): REST-шар над
/// MaintenanceService.StartMaintenanceAsync/EndMaintenanceEarlyAsync — обидва
/// існували з Фази 4 в очікуванні "майбутнього Settings/Maintenance API"
/// (див. коментар класу), просто ніхто їх не викликав з REST.
///
/// GET заразом закриває другу, суміжну прогалину: useMaintenanceWindows()
/// на фронтенді був суто SignalR-стрімом (лише MaintenanceChangedOccurred
/// на кожен Start/End) — уже активні вікна, створені ДО того як хтось
/// відкрив сторінку, були невидимі аж до наступної події. Той самий клас
/// бага, що вже фіксився для Zabbix/RDP/Ping REST-знімків.
/// </summary>
public sealed class MaintenanceController(MaintenanceService maintenance, IOptions<List<ServerEntry>> servers)
    : AdminConsoleControllerBase
{
    [HttpGet]
    public ActionResult<IReadOnlyList<MaintenanceWindow>> Get() => Ok(maintenance.GetActiveWindows());

    /// <summary>
    /// Рівно одне з ServerIp/TargetGroup, обидва звіряються проти реального
    /// appsettings.json-списку (той самий захист від довільного клієнтського
    /// вводу, що вже є в ServersController.Find) — DisplayName формується тут,
    /// а не з клієнта. To рахується на бекенді від DateTimeOffset.Now, а не
    /// приймається як готовий timestamp з фронтенду.
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<MaintenanceWindow>> Start([FromBody] StartMaintenanceRequest request, CancellationToken ct)
    {
        bool hasServer = !string.IsNullOrWhiteSpace(request.ServerIp);
        bool hasGroup = !string.IsNullOrWhiteSpace(request.TargetGroup);

        if (hasServer == hasGroup)
            return BadRequest(new { error = "Вкажіть або serverIp, або targetGroup — рівно одне з двох." });

        if (request.DurationMinutes is { } minutes && minutes <= 0)
            return BadRequest(new { error = "durationMinutes має бути додатним числом, або відсутнім (без обмеження часу)." });

        string displayName;
        string? serverIp = null;
        string? targetGroup = null;

        if (hasServer)
        {
            var server = servers.Value.FirstOrDefault(s => s.IP == request.ServerIp);
            if (server is null)
                return NotFound(new { error = $"Сервер з IP '{request.ServerIp}' не знайдено в конфігурації." });

            serverIp = server.IP;
            displayName = server.Name;
        }
        else
        {
            var group = servers.Value.FirstOrDefault(s =>
                s.Group.Equals(request.TargetGroup, StringComparison.OrdinalIgnoreCase))?.Group;
            if (group is null)
                return NotFound(new { error = $"Групу '{request.TargetGroup}' не знайдено в конфігурації." });

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

    /// <summary>Key — ServerIp або "group:{TargetGroup}" (MaintenanceWindow.Key), як query-параметр — уникає проблем із ':' у маршруті.</summary>
    [HttpDelete]
    public async Task<IActionResult> End([FromQuery] string key, CancellationToken ct)
    {
        bool ended = await maintenance.EndMaintenanceEarlyAsync(key, ct);
        return ended ? NoContent() : NotFound(new { error = $"Активного вікна обслуговування з ключем '{key}' не знайдено." });
    }
}
