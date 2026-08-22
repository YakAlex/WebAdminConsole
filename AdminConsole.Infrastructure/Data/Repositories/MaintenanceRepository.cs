using AdminConsole.Domain.Abstractions;
using AdminConsole.Domain.Models;
using Microsoft.EntityFrameworkCore;

namespace AdminConsole.Infrastructure.Data.Repositories;

public sealed class MaintenanceRepository(AdminConsoleDbContext context)
    : RepositoryBase(context), IMaintenanceRepository
{
    public async Task<IReadOnlyList<MaintenanceWindow>> LoadAllAsync(CancellationToken ct = default) =>
        await Context.MaintenanceWindows.AsNoTracking().ToListAsync(ct);

    /// <summary>Remove+Add by Key — mirrors the old "_windows[window.Key] = window" (ConcurrentDictionary, full value replacement).</summary>
    public async Task UpsertAsync(MaintenanceWindow window, CancellationToken ct = default)
    {
        var existing = await Context.MaintenanceWindows
            .FirstOrDefaultAsync(w => EF.Property<string>(w, "WindowKey") == window.Key, ct);

        if (existing is not null)
            Context.MaintenanceWindows.Remove(existing);

        // The shadow property "WindowKey" MUST be assigned BEFORE transitioning
        // to the Added state — DbSet.Add() immediately requires a non-empty key
        // to add the record to the internal identity map (unlike Detached,
        // where arbitrary properties can be set without restriction).
        var entry = Context.Entry(window);
        entry.Property("WindowKey").CurrentValue = window.Key;
        entry.State = EntityState.Added;

        await SaveChangesAsync(ct);
    }

    public async Task RemoveAsync(string key, CancellationToken ct = default)
    {
        var existing = await Context.MaintenanceWindows
            .FirstOrDefaultAsync(w => EF.Property<string>(w, "WindowKey") == key, ct);
        if (existing is null) return;

        Context.MaintenanceWindows.Remove(existing);
        await SaveChangesAsync(ct);
    }

    public async Task RemoveAllAsync(CancellationToken ct = default)
    {
        var all = await Context.MaintenanceWindows.ToListAsync(ct);
        if (all.Count == 0) return;

        Context.MaintenanceWindows.RemoveRange(all);
        await SaveChangesAsync(ct);
    }
}
