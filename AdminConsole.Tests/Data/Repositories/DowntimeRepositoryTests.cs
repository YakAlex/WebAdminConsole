using AdminConsole.Domain.Abstractions;
using AdminConsole.Domain.Models;
using AdminConsole.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AdminConsole.Tests.Data.Repositories;

public sealed class DowntimeRepositoryTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"AdminConsoleDowntimeRepoTests_{Guid.NewGuid():N}.db");
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

    private static readonly DateTimeOffset FellAt = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static DowntimeRecord MakeRecord(string ip, DateTimeOffset? recoveredAt = null) => new()
    {
        ServerName = ip,
        ServerIp = ip,
        ServerGroup = "Core",
        FellAt = FellAt,
        RecoveredAt = recoveredAt
    };

    [Fact]
    public async Task UpsertAsync_InsertsNewRecord_ByCompositeKey()
    {
        await using var scope = _provider.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IDowntimeRepository>();

        await repo.UpsertAsync(MakeRecord("10.0.0.1"));

        var record = Assert.Single(await repo.LoadAllAsync());
        Assert.Equal("10.0.0.1", record.ServerIp);
        Assert.False(record.IsResolved);
    }

    [Fact]
    public async Task UpsertAsync_SameCompositeKey_UpdatesRecoveredAtAndClosedByMaintenance()
    {
        await using var scope = _provider.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IDowntimeRepository>();
        await repo.UpsertAsync(MakeRecord("10.0.0.1"));

        var recoveredAt = FellAt.AddMinutes(30);
        await repo.UpsertAsync(new DowntimeRecord
        {
            ServerName = "10.0.0.1", ServerIp = "10.0.0.1", ServerGroup = "Core",
            FellAt = FellAt, RecoveredAt = recoveredAt, ClosedByMaintenance = true
        });

        var record = Assert.Single(await repo.LoadAllAsync());
        Assert.Equal(recoveredAt, record.RecoveredAt);
        Assert.True(record.ClosedByMaintenance);
    }

    [Fact]
    public async Task UpsertAsync_DifferentFellAt_SameServer_InsertsASeparateIncident()
    {
        await using var scope = _provider.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IDowntimeRepository>();
        await repo.UpsertAsync(MakeRecord("10.0.0.1"));
        await repo.UpsertAsync(new DowntimeRecord
        {
            ServerName = "10.0.0.1", ServerIp = "10.0.0.1", ServerGroup = "Core",
            FellAt = FellAt.AddDays(1)
        });

        Assert.Equal(2, (await repo.LoadAllAsync()).Count);
    }

    [Fact]
    public async Task DeleteAsync_RemovesByCompositeKey()
    {
        await using var scope = _provider.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IDowntimeRepository>();
        await repo.UpsertAsync(MakeRecord("10.0.0.1"));

        await repo.DeleteAsync("10.0.0.1", FellAt);

        Assert.Empty(await repo.LoadAllAsync());
    }

    [Fact]
    public async Task DeleteAsync_MissingKey_IsANoOp()
    {
        await using var scope = _provider.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IDowntimeRepository>();

        var exception = await Record.ExceptionAsync(() => repo.DeleteAsync("no-such-ip", FellAt));

        Assert.Null(exception);
    }

    [Fact]
    public async Task DeleteAllResolvedAsync_RemovesOnlyResolvedRecords()
    {
        await using var scope = _provider.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IDowntimeRepository>();
        await repo.UpsertAsync(MakeRecord("10.0.0.1", recoveredAt: FellAt.AddMinutes(30))); // resolved
        await repo.UpsertAsync(new DowntimeRecord
        {
            ServerName = "10.0.0.2", ServerIp = "10.0.0.2", ServerGroup = "Core", FellAt = FellAt
        }); // still open

        var removedCount = await repo.DeleteAllResolvedAsync();

        Assert.Equal(1, removedCount);
        var remaining = Assert.Single(await repo.LoadAllAsync());
        Assert.Equal("10.0.0.2", remaining.ServerIp);
    }
}
