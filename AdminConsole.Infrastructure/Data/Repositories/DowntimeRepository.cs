using AdminConsole.Domain.Abstractions;
using AdminConsole.Domain.Models;
using Microsoft.EntityFrameworkCore;

namespace AdminConsole.Infrastructure.Data.Repositories;

public sealed class DowntimeRepository(AdminConsoleDbContext context)
    : RepositoryBase(context), IDowntimeRepository
{
    public async Task<IReadOnlyList<DowntimeRecord>> LoadAllAsync(CancellationToken ct = default) =>
        await Context.DowntimeRecords.AsNoTracking().ToListAsync(ct);

    public async Task UpsertAsync(DowntimeRecord record, CancellationToken ct = default)
    {
        var existing = await Context.DowntimeRecords.FindAsync([record.ServerIp, record.FellAt], ct);

        if (existing is null)
        {
            Context.DowntimeRecords.Add(record);
        }
        else
        {
            existing.RecoveredAt        = record.RecoveredAt;
            existing.ClosedByMaintenance = record.ClosedByMaintenance;
        }

        await SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(string serverIp, DateTimeOffset fellAt, CancellationToken ct = default)
    {
        var existing = await Context.DowntimeRecords.FindAsync([serverIp, fellAt], ct);
        if (existing is null) return;

        Context.DowntimeRecords.Remove(existing);
        await SaveChangesAsync(ct);
    }

    public async Task<int> DeleteAllResolvedAsync(CancellationToken ct = default)
    {
        var resolved = await Context.DowntimeRecords
            .Where(r => r.RecoveredAt != null)
            .ToListAsync(ct);

        if (resolved.Count == 0) return 0;

        Context.DowntimeRecords.RemoveRange(resolved);
        await SaveChangesAsync(ct);
        return resolved.Count;
    }
}
