using AdminConsole.Domain.Abstractions;
using AdminConsole.Domain.Models;
using Microsoft.EntityFrameworkCore;

namespace AdminConsole.Infrastructure.Data.Repositories;

public sealed class MaintenanceRepository(AdminConsoleDbContext context)
    : RepositoryBase(context), IMaintenanceRepository
{
    public async Task<IReadOnlyList<MaintenanceWindow>> LoadAllAsync(CancellationToken ct = default) =>
        await Context.MaintenanceWindows.AsNoTracking().ToListAsync(ct);

    /// <summary>Remove+Add за Key — дзеркалить старе "_windows[window.Key] = window" (ConcurrentDictionary, повна заміна значення).</summary>
    public async Task UpsertAsync(MaintenanceWindow window, CancellationToken ct = default)
    {
        var existing = await Context.MaintenanceWindows
            .FirstOrDefaultAsync(w => EF.Property<string>(w, "WindowKey") == window.Key, ct);

        if (existing is not null)
            Context.MaintenanceWindows.Remove(existing);

        // Тіньова властивість "WindowKey" МАЄ отримати значення ДО переходу
        // в стан Added — DbSet.Add() одразу вимагає непорожній ключ, щоб
        // додати запис в internal identity map (на відміну від Detached,
        // де довільні властивості можна виставляти без обмежень).
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
