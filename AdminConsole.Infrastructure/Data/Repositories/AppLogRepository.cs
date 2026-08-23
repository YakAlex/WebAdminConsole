using AdminConsole.Domain.Abstractions;
using AdminConsole.Domain.Models;
using Microsoft.EntityFrameworkCore;

namespace AdminConsole.Infrastructure.Data.Repositories;

public sealed class AppLogRepository(AdminConsoleDbContext context)
    : RepositoryBase(context), IAppLogRepository
{
    public async Task AppendAsync(AppLogEntry entry, CancellationToken ct = default)
    {
        Context.AppLogEntries.Add(entry);
        await SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<AppLogEntry>> GetRecentAsync(
        int take,
        DateTimeOffset? before = null,
        DateTimeOffset? after  = null,
        string?         search = null,
        CancellationToken ct   = default)
    {
        var query = Context.AppLogEntries.AsNoTracking().AsQueryable();

        if (before is not null)
            query = query.Where(e => e.Timestamp < before.Value);

        if (after is not null)
            query = query.Where(e => e.Timestamp >= after.Value);

        // Step 6 (#10): search over Source/Message. EF Core SQLite translates
        // Contains() into a plain LIKE — unlike ORDER BY DateTimeOffset
        // (see the "SQLite fix" step), there are no provider limitations here.
        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(e => e.Message.Contains(search) || e.Source.Contains(search));

        return await query.OrderByDescending(e => e.Timestamp).Take(take).ToListAsync(ct);
    }

    // Bug fix (2026-08-23, audit Finding 6.1): AppLogEntries had no
    // retention policy and grew forever — every background service logs to
    // it every cycle. ExecuteDeleteAsync (EF Core bulk delete) avoids
    // loading however many million rows might match into memory first.
    // Routed through the same Polly retry pipeline as SaveChangesAsync —
    // bulk operations bypass the change tracker and SaveChangesAsync
    // entirely, so they need their own retry wrapping.
    public Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default) =>
        SqliteRetryPolicy.Pipeline.ExecuteAsync(
            async token => await Context.AppLogEntries.Where(e => e.Timestamp < cutoff).ExecuteDeleteAsync(token),
            ct).AsTask();
}
