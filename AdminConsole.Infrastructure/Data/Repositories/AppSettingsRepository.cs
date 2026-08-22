using AdminConsole.Domain.Abstractions;
using AdminConsole.Domain.Models;
using Microsoft.EntityFrameworkCore;

namespace AdminConsole.Infrastructure.Data.Repositories;

public sealed class AppSettingsRepository(AdminConsoleDbContext context)
    : RepositoryBase(context), IAppSettingsRepository
{
    public async Task<AppSettings> GetAsync(CancellationToken ct = default)
    {
        var existing = await Context.AppSettings.FirstOrDefaultAsync(ct);
        if (existing is not null) return existing;

        var created = new AppSettings();
        Context.AppSettings.Add(created);
        await SaveChangesAsync(ct);
        return created;
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken ct = default)
    {
        var existing = await Context.AppSettings.FirstOrDefaultAsync(ct);
        if (existing is null)
        {
            Context.AppSettings.Add(settings);
        }
        else
        {
            existing.RdpMonitoringEnabled       = settings.RdpMonitoringEnabled;
            existing.ZabbixMonitoringEnabled    = settings.ZabbixMonitoringEnabled;
            existing.BackupMonitoringEnabled    = settings.BackupMonitoringEnabled;
            existing.TelegramPrimaryAdminChatId = settings.TelegramPrimaryAdminChatId;
            existing.RdpDailyPeak                = settings.RdpDailyPeak;
            existing.RdpDailyPeakDate            = settings.RdpDailyPeakDate;
        }

        await SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<TelegramAllowedUser>> GetTelegramAllowedUsersAsync(CancellationToken ct = default) =>
        await Context.TelegramAllowedUsers.AsNoTracking().ToListAsync(ct);

    public async Task UpsertTelegramAllowedUserAsync(long chatId, string? username, CancellationToken ct = default)
    {
        var existing = await Context.TelegramAllowedUsers.FindAsync([chatId], ct);
        if (existing is null)
            Context.TelegramAllowedUsers.Add(new TelegramAllowedUser { ChatId = chatId, Username = username });
        else
            existing.Username = username;

        await SaveChangesAsync(ct);
    }

    public async Task RemoveTelegramAllowedUserAsync(long chatId, CancellationToken ct = default)
    {
        var existing = await Context.TelegramAllowedUsers.FindAsync([chatId], ct);
        if (existing is null) return;

        Context.TelegramAllowedUsers.Remove(existing);
        await SaveChangesAsync(ct);
    }
}
