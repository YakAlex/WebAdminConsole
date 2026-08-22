using AdminConsole.Domain.Abstractions;
using AdminConsole.Domain.Models;
using Microsoft.EntityFrameworkCore;

namespace AdminConsole.Infrastructure.Data.Repositories;

public sealed class AppSettingsRepository(AdminConsoleDbContext context)
    : RepositoryBase(context), IAppSettingsRepository
{
    // Аудит Зона 1, Знахідка №6 (2026-08-22): і GetAsync, і SaveAsync мали
    // "прочитай, якщо нема — створи" без жодного захисту від конкурентних
    // викликів. AppSettings — single-row таблиця (Id — autoincrement PK, БЕЗ
    // додаткового unique-обмеження), а RDP/Zabbix (кожен зі своєю
    // DbContext-сесією через IServiceScopeFactory) викликають GetAsync майже
    // одночасно при старті процесу. Без синхронізації обидва можуть НЕ
    // побачити рядок і обидва вставити свій — не виняток (autoincrement
    // дозволяє два різні Id), а тиха поява ДВОХ рядків AppSettings, після
    // чого різні виклики можуть читати/писати в різні рядки. Статичний gate
    // серіалізує цей шлях у межах процесу — тут єдиний реалістичний масштаб,
    // AdminConsole.Api завжди один процес.
    private static readonly SemaphoreSlim CreateGate = new(1, 1);

    public async Task<AppSettings> GetAsync(CancellationToken ct = default)
    {
        var existing = await Context.AppSettings.FirstOrDefaultAsync(ct);
        if (existing is not null) return existing;

        await CreateGate.WaitAsync(ct);
        try
        {
            // Повторна перевірка під замком — інший виклик (інша DbContext-
            // сесія) міг устигнути створити рядок, поки ми чекали на gate.
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
