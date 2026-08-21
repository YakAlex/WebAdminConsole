using AdminConsole.Domain.Models;
using AdminConsole.Infrastructure.Data;
using AdminConsole.Migration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AdminConsole.Tests.Migration;

/// <summary>
/// T2.7 — фікстурний тест AdminConsole.Migration. Дані нижче — вигадані/
/// анонімізовані (не реальна інфраструктура), але за формою й ключами
/// точно повторюють реальні uptime-*.json/backups.json/maintenance.json/
/// user_settings.json, включно з навмисними дублікатами для перевірки
/// дедуплікації (T2.6) і прострочене Maintenance-вікно для перевірки
/// фільтра "To < now".
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
        // Windows тримає файл-хендл SQLite ще трохи після Dispose() пулу
        // з'єднань — без цього видалення директорії може впасти з IOException.
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
        // 3 унікальних записи з двох файлів (1 дублікат ServerIp+FellAt між файлами прибрано).
        Assert.Equal(3, summary.DowntimeRecords);
        // 2 вікна у файлі, одне прострочене (To в минулому) — відфільтроване, лишається 1.
        Assert.Equal(1, summary.MaintenanceWindows);
        // 3 сирих записи, 2 унікальних ключі (Name|Kind) — дублікат перезаписаний.
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
        // ── uptime: два місячних файли, один запис-дублікат між ними ────────
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

        // ── maintenance: одне прострочене (відфільтроване), одне активне ────
        File.WriteAllText(Path.Combine(_logsDir, "maintenance.json"), """
        [
          { "ServerIp": "10.0.0.4", "TargetGroup": null, "DisplayName": "Server4",
            "From": "2020-01-01T10:00:00+03:00", "To": "2020-01-01T12:00:00+03:00",
            "Reason": "Прострочене", "CreatedAt": "2020-01-01T10:00:00+03:00" },
          { "ServerIp": "10.0.0.5", "TargetGroup": null, "DisplayName": "Server5",
            "From": "2026-01-01T10:00:00+03:00", "To": null,
            "Reason": "Активне без обмеження часу", "CreatedAt": "2026-01-01T10:00:00+03:00" }
        ]
        """);

        // ── backups: 3 сирих записи, дублікат ключа Server1|Full ────────────
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

        // ── user_settings: 2 дозволених telegram-користувачі ─────────────────
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
