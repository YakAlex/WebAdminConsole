using Microsoft.Data.Sqlite;
using Polly;
using Polly.Retry;

namespace AdminConsole.Infrastructure.Data;

/// <summary>
/// Retry policy for SQLITE_BUSY/SQLITE_LOCKED around SaveChangesAsync (T2.3).
/// In WAL mode this is one writer plus many readers without blocking — several
/// concurrent WRITERS (e.g. two Hangfire jobs at the same moment) still end up
/// serialized at the SQLite level. The EF Core SQLite provider doesn't retry
/// these errors on its own. At the scale of "10 servers, a handful of admin
/// requests" this is more of a theoretical safeguard than a real necessity,
/// but it costs only a few lines.
/// </summary>
internal static class SqliteRetryPolicy
{
    private const int SqliteBusy   = 5;
    private const int SqliteLocked = 6;

    public static readonly ResiliencePipeline Pipeline = new ResiliencePipelineBuilder()
        .AddRetry(new RetryStrategyOptions
        {
            ShouldHandle = new PredicateBuilder().Handle<SqliteException>(IsBusyOrLocked),
            MaxRetryAttempts = 3,
            DelayGenerator = _ => new ValueTask<TimeSpan?>(
                TimeSpan.FromMilliseconds(Random.Shared.Next(50, 200)))
        })
        .Build();

    private static bool IsBusyOrLocked(SqliteException ex) =>
        ex.SqliteErrorCode is SqliteBusy or SqliteLocked;
}
