using System.Text.Json;
using System.Text.Json.Serialization;
using AdminConsole.Domain.Abstractions;
using AdminConsole.Domain.Models;
using AdminConsole.Infrastructure.Data;
using AdminConsole.Infrastructure.Data.Entities;
using AdminConsole.Migration.LegacyModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AdminConsole.Migration;

public sealed record MigrationSummary(
    bool AlreadyCompleted,
    int  DowntimeRecords,
    int  MaintenanceWindows,
    int  BackupCheckStates,
    bool AppSettingsMigrated,
    int  TelegramAllowedUsers);

/// <summary>
/// Одноразовий перенос JSON-даних старого WPF AdminConsole у SQLite (Фаза 2,
/// T2.6). Ідемпотентний — MigrationMarker блокує повторний запуск від
/// дублювання даних. Дедуплікація всередині кожного джерела повторює ключі,
/// якими вже користуються відповідні WPF-сервіси:
///   - uptime-*.json   → (ServerIp, FellAt), як UptimeTrackerService.LoadFromDisk
///   - backups.json    → (Name, Kind) = "Name|Kind", як BackupMonitorService.LoadFromDisk
///   - maintenance.json→ Key (ServerIp / "group:X"), як MaintenanceService.LoadFromDisk
///     (+ той самий фільтр прострочених "To < now", що й оригінал)
/// </summary>
public sealed class MigrationRunner(
    AdminConsoleDbContext    db,
    IDowntimeRepository      downtime,
    IMaintenanceRepository   maintenance,
    IBackupStateRepository   backups,
    IAppSettingsRepository   appSettings,
    ILogger<MigrationRunner> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<MigrationSummary> RunAsync(MigrationOptions options, CancellationToken ct = default)
    {
        var marker = await db.MigrationMarkers.FirstOrDefaultAsync(ct);
        if (marker?.CompletedAtUtc is not null)
        {
            logger.LogInformation(
                "Міграція вже виконана {CompletedAt} — повторний запуск нічого не робить.",
                marker.CompletedAtUtc);
            return new MigrationSummary(true, 0, 0, 0, false, 0);
        }

        int downtimeCount    = await MigrateDowntimeAsync(options.OldLogsDirectory, ct);
        int maintenanceCount = await MigrateMaintenanceAsync(options.OldLogsDirectory, ct);
        int backupCount      = await MigrateBackupsAsync(options.OldLogsDirectory, ct);
        var (settingsMigrated, telegramCount) = await MigrateUserSettingsAsync(options.OldUserSettingsPath, ct);

        marker ??= new MigrationMarker();
        marker.CompletedAtUtc = DateTimeOffset.UtcNow;
        if (marker.Id == 0) db.MigrationMarkers.Add(marker);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Міграція завершена: {Downtime} downtime, {Maintenance} maintenance, {Backups} backup-станів, " +
            "settings={Settings}, {Telegram} telegram-користувач(ів).",
            downtimeCount, maintenanceCount, backupCount, settingsMigrated, telegramCount);

        return new MigrationSummary(false, downtimeCount, maintenanceCount, backupCount, settingsMigrated, telegramCount);
    }

    // ── DowntimeRecord: злиття всіх uptime-*.json, дедуп за (ServerIp, FellAt) ──

    private async Task<int> MigrateDowntimeAsync(string logsDir, CancellationToken ct)
    {
        var seen    = new HashSet<(string Ip, DateTimeOffset FellAt)>();
        var records = new List<DowntimeRecord>();

        foreach (var file in FindFiles(logsDir, "uptime-*.json"))
        {
            var loaded = TryDeserialize<List<DowntimeRecord>>(file, JsonOptions);
            foreach (var r in loaded ?? [])
                if (seen.Add((r.ServerIp, r.FellAt)))
                    records.Add(r);
        }

        foreach (var r in records)
            await downtime.UpsertAsync(r, ct);

        return records.Count;
    }

    // ── MaintenanceWindow: фільтр прострочених (як LoadFromDisk), дедуп за Key ──

    private async Task<int> MigrateMaintenanceAsync(string logsDir, CancellationToken ct)
    {
        var path = Path.Combine(logsDir, "maintenance.json");
        if (!File.Exists(path)) return 0;

        var loaded = TryDeserialize<List<MaintenanceWindow>>(path, JsonOptions);

        var now   = DateTimeOffset.Now;
        var byKey = new Dictionary<string, MaintenanceWindow>();
        foreach (var w in loaded ?? [])
            if (w.To is null || w.To >= now)
                byKey[w.Key] = w; // останній перемагає — той самий підхід, що LoadFromDisk

        foreach (var w in byKey.Values)
            await maintenance.UpsertAsync(w, ct);

        return byKey.Count;
    }

    // ── BackupCheckState: дедуп за (Name, Kind) ──────────────────────────────

    private async Task<int> MigrateBackupsAsync(string logsDir, CancellationToken ct)
    {
        var path = Path.Combine(logsDir, "backups.json");
        if (!File.Exists(path)) return 0;

        var opts = new JsonSerializerOptions(JsonOptions);
        opts.Converters.Add(new JsonStringEnumConverter());
        var loaded = TryDeserialize<List<BackupCheckState>>(path, opts);

        var byKey = new Dictionary<string, BackupCheckState>();
        foreach (var s in loaded ?? [])
            byKey[$"{s.Name}|{s.Kind}"] = s; // останній перемагає — той самий підхід, що LoadFromDisk

        foreach (var s in byKey.Values)
            await backups.UpsertAsync(s, ct);

        return byKey.Count;
    }

    // ── UserSettings → AppSettings (мінус CloseToTray) + TelegramAllowedUsers ──

    private async Task<(bool Migrated, int TelegramUsers)> MigrateUserSettingsAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return (false, 0);

        var legacy = TryDeserialize<LegacyUserSettings>(path, JsonOptions);
        if (legacy is null) return (false, 0);

        await appSettings.SaveAsync(new AppSettings
        {
            RdpMonitoringEnabled       = legacy.RdpMonitoringEnabled,
            ZabbixMonitoringEnabled    = legacy.ZabbixMonitoringEnabled,
            BackupMonitoringEnabled    = legacy.BackupMonitoringEnabled,
            TelegramPrimaryAdminChatId = legacy.TelegramPrimaryAdminChatId
        }, ct);

        int count = 0;
        foreach (var chatId in legacy.TelegramAllowedChatIds)
        {
            legacy.TelegramUsernames.TryGetValue(chatId, out var username);
            await appSettings.UpsertTelegramAllowedUserAsync(chatId, username, ct);
            count++;
        }

        return (true, count);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private T? TryDeserialize<T>(string path, JsonSerializerOptions options)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), options);
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Migration: не вдалось прочитати {Path}", path);
            return default;
        }
    }

    private static IEnumerable<string> FindFiles(string dir, string pattern) =>
        Directory.Exists(dir) ? Directory.EnumerateFiles(dir, pattern) : [];
}
