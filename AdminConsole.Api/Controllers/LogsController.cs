using AdminConsole.Domain.Abstractions;
using AdminConsole.Domain.Models;
using Microsoft.AspNetCore.Mvc;

namespace AdminConsole.Api.Controllers;

/// <summary>
/// GET /api/logs?take=1000&amp;before={ts}&amp;after={ts}&amp;search={text} —
/// сторінка AppLogEntries, найновіші перші. Заміна "хвоста найновішого
/// app-*.log" — просто ORDER BY Timestamp DESC LIMIT :take в SQL (T3.9,
/// T4.13). before/after/search — Крок 6 (#10): діапазон дат + пошук.
/// </summary>
public sealed class LogsController(IAppLogRepository repository) : AdminConsoleControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<AppLogEntry>>> Get(
        [FromQuery] int take = 1000,
        [FromQuery] DateTimeOffset? before = null,
        [FromQuery] DateTimeOffset? after = null,
        [FromQuery] string? search = null,
        CancellationToken ct = default) =>
        Ok(await repository.GetRecentAsync(take, before, after, search, ct));
}
