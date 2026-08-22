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
/// One-time transfer of the old WPF AdminConsole's JSON data into SQLite
/// (Phase 2, T2.6). Idempotent — MigrationMarker blocks a re-run from
/// duplicating data. Deduplication within each source mirrors the keys
/// already used by the corresponding WPF services:
///   - uptime-*.json    → (ServerIp, FellAt), same as UptimeTrackerService.LoadFromDisk
///   - backups.json     → (Name, Kind) = "Name|Kind", same as BackupMonitorService.LoadFromDisk
///   - maintenance.json → Key (ServerIp / "group:X"), same as MaintenanceService.LoadFromDisk
///     (+ the same expired-window filter "To < now" as the original)
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
                "Migration was already completed at {CompletedAt} — re-running does nothing.",
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
            "Migration completed: {Downtime} downtime, {Maintenance} maintenance, {Backups} backup state(s), " +
            "settings={Settings}, {Telegram} telegram user(s).",
            downtimeCount, maintenanceCount, backupCount, settingsMigrated, telegramCount);

        return new MigrationSummary(false, downtimeCount, maintenanceCount, backupCount, settingsMigrated, telegramCount);
    }

    // ── DowntimeRecord: merges all uptime-*.json files, dedup by (ServerIp, FellAt) ──

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

    // ── MaintenanceWindow: filters out expired windows (same as LoadFromDisk), dedup by Key ──

    private async Task<int> MigrateMaintenanceAsync(string logsDir, CancellationToken ct)
    {
        var path = Path.Combine(logsDir, "maintenance.json");
        if (!File.Exists(path)) return 0;

        var loaded = TryDeserialize<List<MaintenanceWindow>>(path, JsonOptions);

        var now   = DateTimeOffset.Now;
        var byKey = new Dictionary<string, MaintenanceWindow>();
        foreach (var w in loaded ?? [])
            if (w.To is null || w.To >= now)
                byKey[w.Key] = w; // last one wins — same approach as LoadFromDisk

        foreach (var w in byKey.Values)
            await maintenance.UpsertAsync(w, ct);

        return byKey.Count;
    }

    // ── BackupCheckState: dedup by (Name, Kind) ──────────────────────────────

    private async Task<int> MigrateBackupsAsync(string logsDir, CancellationToken ct)
    {
        var path = Path.Combine(logsDir, "backups.json");
        if (!File.Exists(path)) return 0;

        var opts = new JsonSerializerOptions(JsonOptions);
        opts.Converters.Add(new JsonStringEnumConverter());
        var loaded = TryDeserialize<List<BackupCheckState>>(path, opts);

        var byKey = new Dictionary<string, BackupCheckState>();
        foreach (var s in loaded ?? [])
            byKey[$"{s.Name}|{s.Kind}"] = s; // last one wins — same approach as LoadFromDisk

        foreach (var s in byKey.Values)
            await backups.UpsertAsync(s, ct);

        return byKey.Count;
    }

    // ── UserSettings → AppSettings (minus CloseToTray) + TelegramAllowedUsers ──

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
            logger.LogError(ex, "Migration: failed to read {Path}", path);
            return default;
        }
    }

    private static IEnumerable<string> FindFiles(string dir, string pattern) =>
        Directory.Exists(dir) ? Directory.EnumerateFiles(dir, pattern) : [];
}
