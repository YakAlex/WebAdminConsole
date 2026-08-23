using AdminConsole.Domain.Abstractions;
using AdminConsole.Domain.Models;
using AdminConsole.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AdminConsole.Tests.Data.Repositories;

public sealed class AppSettingsRepositoryTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"AdminConsoleAppSettingsRepoTests_{Guid.NewGuid():N}.db");
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAdminConsoleDb($"Data Source={_dbPath}");
        _provider = services.BuildServiceProvider();

        await using var scope = _provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AdminConsoleDbContext>().Database.MigrateAsync();
    }

    public Task DisposeAsync()
    {
        _provider.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        foreach (var ext in new[] { "-shm", "-wal" })
            if (File.Exists(_dbPath + ext)) File.Delete(_dbPath + ext);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task GetAsync_OnAnEmptyDatabase_CreatesAndReturnsADefaultRow()
    {
        await using var scope = _provider.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IAppSettingsRepository>();

        var settings = await repo.GetAsync();

        Assert.True(settings.RdpMonitoringEnabled);
        Assert.True(settings.ZabbixMonitoringEnabled);
        Assert.True(settings.BackupMonitoringEnabled);
    }

    [Fact]
    public async Task GetAsync_CalledTwice_ReturnsTheSameSingleRow()
    {
        await using var scope = _provider.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IAppSettingsRepository>();

        await repo.GetAsync();
        await repo.GetAsync();

        await using var db = scope.ServiceProvider.GetRequiredService<AdminConsoleDbContext>();
        Assert.Equal(1, await db.AppSettings.CountAsync());
    }

    [Fact]
    public async Task SaveAsync_OverwritesAllFieldsOnTheExistingRow()
    {
        await using var scope = _provider.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IAppSettingsRepository>();
        await repo.GetAsync(); // creates the default row

        await repo.SaveAsync(new AppSettings
        {
            RdpMonitoringEnabled = false,
            ZabbixMonitoringEnabled = false,
            BackupMonitoringEnabled = false,
            TelegramPrimaryAdminChatId = 12345,
            RdpDailyPeak = 7,
            RdpDailyPeakDate = new DateTime(2026, 1, 1)
        });

        var reloaded = await repo.GetAsync();
        Assert.False(reloaded.RdpMonitoringEnabled);
        Assert.False(reloaded.ZabbixMonitoringEnabled);
        Assert.False(reloaded.BackupMonitoringEnabled);
        Assert.Equal(12345, reloaded.TelegramPrimaryAdminChatId);
        Assert.Equal(7, reloaded.RdpDailyPeak);
    }

    [Fact]
    public async Task UpdateRdpDailyPeakAsync_AndUpdateMonitoringTogglesAsync_ConcurrentlyOnDifferentFields_NeitherLosesTheOthersChange()
    {
        // Regression guard for the "targeted update methods only touch their
        // own fields" fix (Zone 2, Finding #2) — two concurrent calls
        // changing DIFFERENT fields on the same row must not clobber each
        // other via a stale full-object copy.
        await using (var seedScope = _provider.CreateAsyncScope())
            await seedScope.ServiceProvider.GetRequiredService<IAppSettingsRepository>().GetAsync();

        await using var scopeA = _provider.CreateAsyncScope();
        await using var scopeB = _provider.CreateAsyncScope();
        var repoA = scopeA.ServiceProvider.GetRequiredService<IAppSettingsRepository>();
        var repoB = scopeB.ServiceProvider.GetRequiredService<IAppSettingsRepository>();

        await Task.WhenAll(
            repoA.UpdateRdpDailyPeakAsync(42, new DateTime(2026, 3, 1)),
            repoB.UpdateMonitoringTogglesAsync(rdpEnabled: false, zabbixEnabled: false, backupEnabled: false));

        await using var verifyScope = _provider.CreateAsyncScope();
        var settings = await verifyScope.ServiceProvider.GetRequiredService<IAppSettingsRepository>().GetAsync();
        Assert.Equal(42, settings.RdpDailyPeak);
        Assert.False(settings.RdpMonitoringEnabled);
    }

    /// <summary>
    /// The concurrency guard this repository was already fixed for (Zone 1,
    /// Finding #6): two concurrent first-ever GetAsync calls (a fresh DB,
    /// e.g. RDP and Zabbix polling starting up simultaneously) must not both
    /// insert their own default row.
    /// </summary>
    [Fact]
    public async Task GetAsync_ManyConcurrentFirstCalls_CreateExactlyOneRow()
    {
        const int concurrentCallers = 20;

        var tasks = Enumerable.Range(0, concurrentCallers).Select(async _ =>
        {
            await using var scope = _provider.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IAppSettingsRepository>().GetAsync();
        });

        var exception = await Record.ExceptionAsync(() => Task.WhenAll(tasks));
        Assert.Null(exception);

        await using var verifyScope = _provider.CreateAsyncScope();
        await using var db = verifyScope.ServiceProvider.GetRequiredService<AdminConsoleDbContext>();
        Assert.Equal(1, await db.AppSettings.CountAsync());
    }

    [Fact]
    public async Task TelegramAllowedUsers_UpsertThenRemove_RoundTrips()
    {
        await using var scope = _provider.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IAppSettingsRepository>();

        await repo.UpsertTelegramAllowedUserAsync(1001, "alice");
        var afterAdd = await repo.GetTelegramAllowedUsersAsync();
        Assert.Single(afterAdd, u => u.ChatId == 1001 && u.Username == "alice");

        await repo.UpsertTelegramAllowedUserAsync(1001, "alice-renamed");
        var afterRename = await repo.GetTelegramAllowedUsersAsync();
        Assert.Single(afterRename, u => u.ChatId == 1001 && u.Username == "alice-renamed");

        await repo.RemoveTelegramAllowedUserAsync(1001);
        Assert.Empty(await repo.GetTelegramAllowedUsersAsync());
    }
}
