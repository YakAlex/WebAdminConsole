using AdminConsole.Domain.Abstractions;
using AdminConsole.Domain.Models;
using Microsoft.AspNetCore.Mvc;

namespace AdminConsole.Api.Controllers;

/// <summary>
/// GET /api/logs?take=1000&amp;before={ts}&amp;after={ts}&amp;search={text} —
/// paginated AppLogEntries, newest first. Replaces "tailing the newest
/// app-*.log" — just an ORDER BY Timestamp DESC LIMIT :take in SQL (T3.9,
/// T4.13). before/after/search — Step 6 (#10): date range + search.
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
