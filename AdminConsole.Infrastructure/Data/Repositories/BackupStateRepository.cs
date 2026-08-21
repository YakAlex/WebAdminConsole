using AdminConsole.Domain.Abstractions;
using AdminConsole.Domain.Models;
using Microsoft.EntityFrameworkCore;

namespace AdminConsole.Infrastructure.Data.Repositories;

public sealed class BackupStateRepository(AdminConsoleDbContext context)
    : RepositoryBase(context), IBackupStateRepository
{
    public async Task<IReadOnlyList<BackupCheckState>> LoadAllAsync(CancellationToken ct = default) =>
        await Context.BackupCheckStates.AsNoTracking().Include(s => s.History).ToListAsync(ct);

    public async Task UpsertAsync(BackupCheckState state, CancellationToken ct = default)
    {
        var existing = await Context.BackupCheckStates
            .Include(s => s.History)
            .FirstOrDefaultAsync(s => s.Name == state.Name && s.Kind == state.Kind, ct);

        if (existing is null)
        {
            Context.BackupCheckStates.Add(state);
        }
        else
        {
            existing.Host                    = state.Host;
            existing.Outcome                 = state.Outcome;
            existing.LastConfirmedAt         = state.LastConfirmedAt;
            existing.LastConfirmedOutcome    = state.LastConfirmedOutcome;
            existing.ConsecutiveUnknownCount = state.ConsecutiveUnknownCount;
            existing.ConsecutiveBadCount     = state.ConsecutiveBadCount;
            existing.LastRawOutcome          = state.LastRawOutcome;
            existing.LastError               = state.LastError;

            existing.History.Clear();
            foreach (var sample in state.History)
                existing.History.Add(new BackupSample { ObservedAt = sample.ObservedAt, SizeBytes = sample.SizeBytes });
        }

        await SaveChangesAsync(ct);
    }

    public async Task<int> DeleteWhereKeyNotInAsync(IReadOnlySet<string> validKeys, CancellationToken ct = default)
    {
        var all = await Context.BackupCheckStates.ToListAsync(ct);
        var toRemove = all.Where(s => !validKeys.Contains($"{s.Name}|{s.Kind}")).ToList();
        if (toRemove.Count == 0) return 0;

        Context.BackupCheckStates.RemoveRange(toRemove);
        await SaveChangesAsync(ct);
        return toRemove.Count;
    }
}
