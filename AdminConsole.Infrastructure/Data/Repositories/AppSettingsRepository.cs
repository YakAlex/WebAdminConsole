using AdminConsole.Domain.Abstractions;
using AdminConsole.Domain.Models;
using Microsoft.EntityFrameworkCore;

namespace AdminConsole.Infrastructure.Data.Repositories;

public sealed class AppSettingsRepository(AdminConsoleDbContext context)
    : RepositoryBase(context), IAppSettingsRepository
{
    // Audit Zone 1, Finding #6 (2026-08-22): both GetAsync and SaveAsync had a
    // "read, if missing — create" pattern with no protection against concurrent
    // calls. AppSettings is a single-row table (Id is an autoincrement PK, WITH
    // NO additional unique constraint), and RDP/Zabbix (each with its own
    // DbContext session via IServiceScopeFactory) call GetAsync nearly
    // simultaneously at process startup. Without synchronization both could
    // fail to see the row and both insert their own — not an exception
    // (autoincrement allows two different Ids), but the silent appearance of
    // TWO AppSettings rows, after which different calls could read/write
    // different rows. A static gate serializes this path within the process —
    // the only realistic scale here, since AdminConsole.Api is always a single process.
    private static readonly SemaphoreSlim CreateGate = new(1, 1);

    public Task<AppSettings> GetAsync(CancellationToken ct = default) => GetTrackedAsync(ct);

    // Audit Zone 2, Finding #2 (2026-08-22): UpdateMonitoringTogglesAsync/
    // UpdateRdpDailyPeakAsync/UpdateTelegramPrimaryAdminAsync replaced
    // GetAsync+SaveAsync(full object) in their three callers
    // (MonitoringController, RdpMonitorService, TelegramAccessControlService).
    // Each method touches ONLY its own fields on the tracked entity — by
    // default EF Core generates an UPDATE covering only the columns marked
    // "modified", so two concurrent calls that change DIFFERENT fields on the
    // same row (e.g. the RDP peak and the monitoring toggles at the same
    // time) can no longer overwrite each other's changes with their own stale
    // copy of the remaining fields (lost update — previously SaveAsync/ApplyTo
    // copied ALL fields together).
    public async Task UpdateMonitoringTogglesAsync(
        bool rdpEnabled, bool zabbixEnabled, bool backupEnabled, CancellationToken ct = default)
    {
        var settings = await GetTrackedAsync(ct);
        settings.RdpMonitoringEnabled    = rdpEnabled;
        settings.ZabbixMonitoringEnabled = zabbixEnabled;
        settings.BackupMonitoringEnabled = backupEnabled;
        await SaveChangesAsync(ct);
    }

    public async Task UpdateRdpDailyPeakAsync(int peak, DateTime date, CancellationToken ct = default)
    {
        var settings = await GetTrackedAsync(ct);
        settings.RdpDailyPeak     = peak;
        settings.RdpDailyPeakDate = date;
        await SaveChangesAsync(ct);
    }

    public async Task UpdateTelegramPrimaryAdminAsync(long chatId, CancellationToken ct = default)
    {
        var settings = await GetTrackedAsync(ct);
        settings.TelegramPrimaryAdminChatId = chatId;
        await SaveChangesAsync(ct);
    }

    public async Task UpdateZabbixMinSeverityAsync(int minSeverity, CancellationToken ct = default)
    {
        var settings = await GetTrackedAsync(ct);
        settings.ZabbixMinSeverity = minSeverity;
        await SaveChangesAsync(ct);
    }

    private async Task<AppSettings> GetTrackedAsync(CancellationToken ct)
    {
        var existing = await Context.AppSettings.FirstOrDefaultAsync(ct);
        if (existing is not null) return existing;

        await CreateGate.WaitAsync(ct);
        try
        {
            // Re-check under the lock — another call (a different DbContext
            // session) may have created the row while we were waiting on the gate.
            existing = await Context.AppSettings.FirstOrDefaultAsync(ct);
            if (existing is not null) return existing;

            var created = new AppSettings();
            Context.AppSettings.Add(created);
            await SaveChangesAsync(ct);
            return created;
        }
        finally
        {
            CreateGate.Release();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken ct = default)
    {
        var existing = await Context.AppSettings.FirstOrDefaultAsync(ct);
        if (existing is not null)
        {
            ApplyTo(existing, settings);
            await SaveChangesAsync(ct);
            return;
        }

        await CreateGate.WaitAsync(ct);
        try
        {
            existing = await Context.AppSettings.FirstOrDefaultAsync(ct);
            if (existing is not null)
            {
                ApplyTo(existing, settings);
            }
            else
            {
                Context.AppSettings.Add(settings);
            }

            await SaveChangesAsync(ct);
        }
        finally
        {
            CreateGate.Release();
        }
    }

    private static void ApplyTo(AppSettings existing, AppSettings settings)
    {
        existing.RdpMonitoringEnabled       = settings.RdpMonitoringEnabled;
        existing.ZabbixMonitoringEnabled    = settings.ZabbixMonitoringEnabled;
        existing.BackupMonitoringEnabled    = settings.BackupMonitoringEnabled;
        existing.TelegramPrimaryAdminChatId = settings.TelegramPrimaryAdminChatId;
        existing.RdpDailyPeak               = settings.RdpDailyPeak;
        existing.RdpDailyPeakDate           = settings.RdpDailyPeakDate;
        existing.ZabbixMinSeverity          = settings.ZabbixMinSeverity;
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
