using AdminConsole.Domain.Abstractions;
using AdminConsole.Domain.Models;
using AdminConsole.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AdminConsole.Tests.Data.Repositories;

public sealed class AppLogRepositoryTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"AdminConsoleAppLogRepoTests_{Guid.NewGuid():N}.db");
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

    private static readonly DateTimeOffset Now = new(2026, 1, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task GetRecentAsync_OrdersNewestFirst_AndRespectsTake()
    {
        await using var scope = _provider.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IAppLogRepository>();
        await repo.AppendAsync(new AppLogEntry(LogSeverity.Info, "Test", "first", Now.AddMinutes(-2)));
        await repo.AppendAsync(new AppLogEntry(LogSeverity.Info, "Test", "second", Now.AddMinutes(-1)));
        await repo.AppendAsync(new AppLogEntry(LogSeverity.Info, "Test", "third", Now));

        var recent = await repo.GetRecentAsync(take: 2);

        Assert.Equal(2, recent.Count);
        Assert.Equal("third", recent[0].Message);
        Assert.Equal("second", recent[1].Message);
    }

    [Fact]
    public async Task GetRecentAsync_FiltersByBeforeAndAfter()
    {
        await using var scope = _provider.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IAppLogRepository>();
        await repo.AppendAsync(new AppLogEntry(LogSeverity.Info, "Test", "too-old", Now.AddHours(-2)));
        await repo.AppendAsync(new AppLogEntry(LogSeverity.Info, "Test", "in-range", Now.AddHours(-1)));
        await repo.AppendAsync(new AppLogEntry(LogSeverity.Info, "Test", "too-new", Now.AddHours(1)));

        var results = await repo.GetRecentAsync(take: 100, before: Now, after: Now.AddHours(-1.5));

        var entry = Assert.Single(results);
        Assert.Equal("in-range", entry.Message);
    }

    [Fact]
    public async Task GetRecentAsync_FiltersBySearch_AcrossMessageAndSource()
    {
        await using var scope = _provider.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IAppLogRepository>();
        await repo.AppendAsync(new AppLogEntry(LogSeverity.Info, "Backup", "everything ok", Now));
        await repo.AppendAsync(new AppLogEntry(LogSeverity.Error, "Zabbix", "connection failed", Now));

        var bySource = await repo.GetRecentAsync(take: 100, search: "Backup");
        Assert.Single(bySource, e => e.Source == "Backup");

        var byMessage = await repo.GetRecentAsync(take: 100, search: "failed");
        Assert.Single(byMessage, e => e.Message == "connection failed");
    }

    [Fact]
    public async Task DeleteOlderThanAsync_DeletesOnlyStrictlyOlderEntries_LeavesTheCutoffItself()
    {
        await using var scope = _provider.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IAppLogRepository>();
        var cutoff = Now.AddDays(-90);
        await repo.AppendAsync(new AppLogEntry(LogSeverity.Info, "Test", "older", cutoff.AddSeconds(-1)));
        await repo.AppendAsync(new AppLogEntry(LogSeverity.Info, "Test", "exactly-at-cutoff", cutoff));
        await repo.AppendAsync(new AppLogEntry(LogSeverity.Info, "Test", "newer", cutoff.AddSeconds(1)));

        var removed = await repo.DeleteOlderThanAsync(cutoff);

        Assert.Equal(1, removed);
        var remaining = await repo.GetRecentAsync(take: 100);
        Assert.Equal(2, remaining.Count);
        Assert.DoesNotContain(remaining, e => e.Message == "older");
    }

    [Fact]
    public async Task DeleteOlderThanAsync_NothingToDelete_ReturnsZero()
    {
        await using var scope = _provider.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IAppLogRepository>();
        await repo.AppendAsync(new AppLogEntry(LogSeverity.Info, "Test", "recent", Now));

        var removed = await repo.DeleteOlderThanAsync(Now.AddDays(-90));

        Assert.Equal(0, removed);
    }
}
