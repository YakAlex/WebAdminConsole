using AdminConsole.Domain.Models;

namespace AdminConsole.Domain.Abstractions;

/// <summary>
/// Persistence for AppLogEntry — replaces the file-based app-YYYY-MM-DD.log
/// (FileLoggerService, T4.13: the service is removed entirely). Eliminates
/// the whole class of multi-file merge / "tail of the newest file" problems —
/// this is just ORDER BY Timestamp DESC LIMIT :take in SQL (T3.9: GET /api/logs).
/// </summary>
public interface IAppLogRepository
{
    Task AppendAsync(AppLogEntry entry, CancellationToken ct = default);

    /// <summary>
    /// Most recent entries, newest first. <paramref name="before"/>/<paramref name="after"/>
    /// bound the date range (Step 6, #10 — search + date filtering on Logs),
    /// <paramref name="search"/> is a substring match against Source/Message
    /// (case-insensitive via SQLite's default LIKE behavior).
    /// </summary>
    Task<IReadOnlyList<AppLogEntry>> GetRecentAsync(
        int take,
        DateTimeOffset? before = null,
        DateTimeOffset? after  = null,
        string?         search = null,
        CancellationToken ct   = default);

    /// <summary>Deletes every entry with Timestamp strictly older than cutoff. Returns the count removed.</summary>
    Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default);
}
