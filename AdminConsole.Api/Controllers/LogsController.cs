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
    // Bug fix (2026-08-23, audit Finding 5.1): take had no upper bound and
    // flowed straight into EF Core's Take() against a table with no
    // retention policy of its own (see AppLogRetentionJob) — a single
    // request with an absurd take forced a full-table sort+serialize.
    private const int MaxTake = 5000;

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<AppLogEntry>>> Get(
        [FromQuery] int take = 1000,
        [FromQuery] DateTimeOffset? before = null,
        [FromQuery] DateTimeOffset? after = null,
        [FromQuery] string? search = null,
        CancellationToken ct = default) =>
        Ok(await repository.GetRecentAsync(Math.Clamp(take, 1, MaxTake), before, after, search, ct));
}
