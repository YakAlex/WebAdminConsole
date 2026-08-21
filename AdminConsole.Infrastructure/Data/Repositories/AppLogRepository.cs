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

        // Крок 6 (#10): пошук по Source/Message. EF Core SQLite транслює
        // Contains() у звичайний LIKE — на відміну від ORDER BY DateTimeOffset
        // (Крок "SQLite fix"), тут жодних обмежень провайдера немає.
        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(e => e.Message.Contains(search) || e.Source.Contains(search));

        return await query.OrderByDescending(e => e.Timestamp).Take(take).ToListAsync(ct);
    }
}
