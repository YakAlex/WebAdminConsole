using AdminConsole.Domain.Abstractions;
using AdminConsole.Domain.Models;
using Microsoft.EntityFrameworkCore;

namespace AdminConsole.Infrastructure.Data.Repositories;

public sealed class MaintenanceRepository(AdminConsoleDbContext context)
    : RepositoryBase(context), IMaintenanceRepository
{
    // Bug fix (2026-08-23, audit Finding 6.2): UpsertAsync had the same
    // "read, if missing then mutate" pattern AppSettingsRepository was
    // already fixed for (CreateGate there) — but here WindowKey is an
    // actual primary key, not just a conceptual single row, so the failure
    // mode is sharper: two concurrent StartMaintenance calls for the SAME
    // key (two different DbContext scopes — MaintenanceService gets a fresh
    // scope per call via IServiceScopeFactory) can both read "no existing
    // row" before either has written, then both attempt an INSERT with the
    // same WindowKey — the second SaveChangesAsync throws a raw, unhandled
    // DbUpdateException (UNIQUE constraint failed) instead of the second
    // call cleanly replacing the first. A static gate serializes this path
    // within the process — the only realistic scale here, since
    // AdminConsole.Api is always a single process (same reasoning as
    // AppSettingsRepository.CreateGate).
    private static readonly SemaphoreSlim CreateGate = new(1, 1);

    public async Task<IReadOnlyList<MaintenanceWindow>> LoadAllAsync(CancellationToken ct = default) =>
        await Context.MaintenanceWindows.AsNoTracking().ToListAsync(ct);

    /// <summary>Remove+Add by Key — mirrors the old "_windows[window.Key] = window" (ConcurrentDictionary, full value replacement).</summary>
    public async Task UpsertAsync(MaintenanceWindow window, CancellationToken ct = default)
    {
        await CreateGate.WaitAsync(ct);
        try
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
        finally
        {
            CreateGate.Release();
        }
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
