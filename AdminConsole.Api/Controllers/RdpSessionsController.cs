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
/// GET /api/rdp-sessions — живий знімок RDP-сесій ЗАРАЗ (реально опитує
/// termінальні сервери через quser в момент запиту) — для початкового
/// завантаження сторінки RDP Sessions (Фаза 10, Крок 11.2 аудиту).
/// </summary>
public sealed class RdpSessionsController(RdpMonitorService rdpMonitor) : AdminConsoleControllerBase
{
    [HttpGet]
    public async Task<ActionResult<RdpSnapshotPayload>> Get(CancellationToken ct)
    {
        var (sessions, peak, lastUser, lastServer, lastAt) = await rdpMonitor.GetSnapshotNowAsync(ct);
        return Ok(new RdpSnapshotPayload(sessions, peak, lastUser, lastServer, lastAt));
    }
}
