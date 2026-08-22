using AdminConsole.Domain.Models;
using AdminConsole.Infrastructure.Data;
using AdminConsole.Migration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AdminConsole.Tests.Migration;

/// <summary>
/// T2.7 — fixture test for AdminConsole.Migration. The data below is
/// made-up/anonymized (not real infrastructure), but matches the shape and
/// keys of the real uptime-*.json/backups.json/maintenance.json/
/// user_settings.json files exactly, including deliberate duplicates to
/// verify deduplication (T2.6) and an expired Maintenance window to verify
/// the "To < now" filter.
/// </summary>
public sealed class MigrationRunnerTests : IAsyncLifetime
{
    private readonly string _rootDir = Path.Combine(Path.GetTempPath(), "AdminConsoleMigrationTests_" + Guid.NewGuid());
    private string _logsDir  = null!;
    private string _userSettingsPath = null!;
    private string _dbPath   = null!;
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync()
    {
        _logsDir = Path.Combine(_rootDir, "logs");
        Directory.CreateDirectory(_logsDir);
        _userSettingsPath = Path.Combine(_rootDir, "user_settings.json");
        _dbPath = Path.Combine(_rootDir, "test.db");

        WriteFixtures();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAdminConsoleDb($"Data Source={_dbPath}");
        services.AddScoped<MigrationRunner>();
        _provider = services.BuildServiceProvider();

        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AdminConsoleDbContext>();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync()
    {
        _provider.Dispose();
        // Windows holds the SQLite file handle for a bit after the
        // connection pool's Dispose() — without this, deleting the directory
        // can fail with an IOException.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_rootDir))
            Directory.Delete(_rootDir, recursive: true);
        return Task.CompletedTask;
    }

    private MigrationOptions Options => new()
    {
        OldLogsDirectory    = _logsDir,
        OldUserSettingsPath = _userSettingsPath
    };

    [Fact]
    public async Task FirstRun_MigratesFixtureData_WithDeduplicationAndExpiredFilter()
    {
        await using var scope = _provider.CreateAsyncScope();
        var runner = scope.ServiceProvider.GetRequiredService<MigrationRunner>();

        var summary = await runner.RunAsync(Options);

        Assert.False(summary.AlreadyCompleted);
        // 3 unique records from two files (1 duplicate ServerIp+FellAt across files removed).
        Assert.Equal(3, summary.DowntimeRecords);
        // 2 windows in the file, one expired (To in the past) — filtered out, 1 remains.
        Assert.Equal(1, summary.MaintenanceWindows);
        // 3 raw records, 2 unique keys (Name|Kind) — the duplicate is overwritten.
        Assert.Equal(2, summary.BackupCheckStates);
        Assert.True(summary.AppSettingsMigrated);
        Assert.Equal(2, summary.TelegramAllowedUsers);

        await using var db = scope.ServiceProvider.GetRequiredService<AdminConsoleDbContext>();
        Assert.Equal(3, await db.DowntimeRecords.CountAsync());
        Assert.Equal(1, await db.MaintenanceWindows.CountAsync());
        Assert.Equal(2, await db.BackupCheckStates.CountAsync());
        Assert.Equal(2, await db.TelegramAllowedUsers.CountAsync());

        var settings = await db.AppSettings.SingleAsync();
        Assert.False(settings.RdpMonitoringEnabled);
        Assert.True(settings.ZabbixMonitoringEnabled);
        Assert.Equal(555000111L, settings.TelegramPrimaryAdminChatId);

        var backupWithHistory = await db.BackupCheckStates
            .Include(s => s.History)
            .FirstAsync(s => s.Name == "Server1" && s.Kind == BackupKind.Full);
        Assert.Equal(2, backupWithHistory.History.Count);
    }

    [Fact]
    public async Task SecondRun_IsIdempotent_NoDuplicateRows()
    {
        await using (var scope = _provider.CreateAsyncScope())
        {
            var runner = scope.ServiceProvider.GetRequiredService<MigrationRunner>();
            await runner.RunAsync(Options);
        }

        MigrationSummary second;
        await using (var scope = _provider.CreateAsyncScope())
        {
            var runner = scope.ServiceProvider.GetRequiredService<MigrationRunner>();
            second = await runner.RunAsync(Options);
        }

        Assert.True(second.AlreadyCompleted);

        await using var verifyScope = _provider.CreateAsyncScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<AdminConsoleDbContext>();
        Assert.Equal(3, await db.DowntimeRecords.CountAsync());
        Assert.Equal(1, await db.MaintenanceWindows.CountAsync());
        Assert.Equal(2, await db.BackupCheckStates.CountAsync());
        Assert.Equal(2, await db.TelegramAllowedUsers.CountAsync());
    }

    private void WriteFixtures()
    {
        // ── uptime: two monthly files, one duplicate record between them ────
        File.WriteAllText(Path.Combine(_logsDir, "uptime-2026-06.json"), """
        [
          { "ServerName": "Server1", "ServerIp": "10.0.0.1", "ServerGroup": "Core",
            "FellAt": "2026-06-19T10:24:15+03:00", "RecoveredAt": "2026-06-19T10:31:42+03:00",
            "ClosedByMaintenance": false },
          { "ServerName": "Server2", "ServerIp": "10.0.0.2", "ServerGroup": "Core",
            "FellAt": "2026-06-25T08:00:00+03:00", "RecoveredAt": null,
            "ClosedByMaintenance": false }
        ]
        """);
        File.WriteAllText(Path.Combine(_logsDir, "uptime-2026-07.json"), """
        [
          { "ServerName": "Server1", "ServerIp": "10.0.0.1", "ServerGroup": "Core",
            "FellAt": "2026-06-19T10:24:15+03:00", "RecoveredAt": "2026-06-19T10:31:42+03:00",
            "ClosedByMaintenance": false },
          { "ServerName": "Server3", "ServerIp": "10.0.0.3", "ServerGroup": "Edge",
            "FellAt": "2026-07-02T14:10:00+03:00", "RecoveredAt": "2026-07-02T14:20:00+03:00",
            "ClosedByMaintenance": true }
        ]
        """);

        // ── maintenance: one expired (filtered out), one active ─────────────
        File.WriteAllText(Path.Combine(_logsDir, "maintenance.json"), """
        [
          { "ServerIp": "10.0.0.4", "TargetGroup": null, "DisplayName": "Server4",
            "From": "2020-01-01T10:00:00+03:00", "To": "2020-01-01T12:00:00+03:00",
            "Reason": "Expired", "CreatedAt": "2020-01-01T10:00:00+03:00" },
          { "ServerIp": "10.0.0.5", "TargetGroup": null, "DisplayName": "Server5",
            "From": "2026-01-01T10:00:00+03:00", "To": null,
            "Reason": "Active with no time limit", "CreatedAt": "2026-01-01T10:00:00+03:00" }
        ]
        """);

        // ── backups: 3 raw records, duplicate key Server1|Full ──────────────
        File.WriteAllText(Path.Combine(_logsDir, "backups.json"), """
        [
          { "Name": "Server1", "Host": "server1.local", "Kind": "Full", "Outcome": "Ok",
            "LastConfirmedAt": "2026-08-01T03:00:00+03:00", "LastConfirmedOutcome": "Ok",
            "ConsecutiveUnknownCount": 0, "ConsecutiveBadCount": 0, "LastRawOutcome": null,
            "LastError": null,
            "History": [
              { "ObservedAt": "2026-07-30T03:00:00+03:00", "SizeBytes": 1000000 },
              { "ObservedAt": "2026-07-31T03:00:00+03:00", "SizeBytes": 1050000 }
            ] },
          { "Name": "Server1", "Host": "server1.local", "Kind": "Full", "Outcome": "Stale",
            "LastConfirmedAt": "2026-08-02T03:00:00+03:00", "LastConfirmedOutcome": "Stale",
            "ConsecutiveUnknownCount": 0, "ConsecutiveBadCount": 0, "LastRawOutcome": null,
            "LastError": null,
            "History": [
              { "ObservedAt": "2026-07-30T03:00:00+03:00", "SizeBytes": 1000000 },
              { "ObservedAt": "2026-07-31T03:00:00+03:00", "SizeBytes": 1050000 }
            ] },
          { "Name": "Server2", "Host": "server2.local", "Kind": "Diff", "Outcome": "Missing",
            "LastConfirmedAt": null, "LastConfirmedOutcome": null,
            "ConsecutiveUnknownCount": 2, "ConsecutiveBadCount": 1, "LastRawOutcome": "Missing",
            "LastError": "Share unreachable", "History": [] }
        ]
        """);

        // ── user_settings: 2 allowed telegram users ──────────────────────────
        File.WriteAllText(_userSettingsPath, """
        {
          "CloseToTray": true,
          "RdpMonitoringEnabled": false,
          "ZabbixMonitoringEnabled": true,
          "BackupMonitoringEnabled": true,
          "TelegramPrimaryAdminChatId": 555000111,
          "TelegramAllowedChatIds": [ 111222333, 444555666 ],
          "TelegramUsernames": {
            "111222333": "admin_ivan",
            "444555666": "admin_olena"
          }
        }
        """);
    }
}
