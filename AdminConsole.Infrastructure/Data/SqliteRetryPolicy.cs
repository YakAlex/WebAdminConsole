using Microsoft.Data.Sqlite;
using Polly;
using Polly.Retry;

namespace AdminConsole.Infrastructure.Data;

/// <summary>
/// Retry-політика на SQLITE_BUSY/SQLITE_LOCKED навколо SaveChangesAsync (T2.3).
/// У WAL-режимі це один writer + багато читачів без блокувань — кілька
/// одночасних ПИСАЧІВ (напр. дві Hangfire-джоби в один момент) усе одно
/// серіалізуються на рівні SQLite. EF Core SQLite-провайдер сам такі помилки
/// не ретраїть. При масштабі "10 серверів, кілька admin-запитів" це радше
/// теоретичний захист, ніж реальна необхідність, але коштує кілька рядків.
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
