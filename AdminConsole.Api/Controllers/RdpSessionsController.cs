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
///
/// Контрольна перевірка (2026-08-21): базовий [Route("api/[controller]")]
/// з AdminConsoleControllerBase резолвить [controller] буквально в
/// "RdpSessions" (без дефіса) — реальний маршрут був /api/RdpSessions,
/// тоді як фронтенд (endpoints.ts) завжди звертався на /api/rdp-sessions.
/// Це давало 404 на КОЖЕН запит ще ДО апаратно-мережевого рівня — найбільш
/// ймовірна першопричина "HTTP 0/unknown error" з Пункту 1 (справжній 404
/// перехоплювався б як ApiError(404, ...), але саме ця розбіжність шляхів
/// підтверджена живим запитом до застосунку — виправлено явним [Route].
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
