using AdminConsole.Domain.Abstractions;
using AdminConsole.Domain.Models;
using AdminConsole.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AdminConsole.Tests.Data.Repositories;

public sealed class MaintenanceRepositoryTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"AdminConsoleMaintenanceRepoTests_{Guid.NewGuid():N}.db");
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

    private static MaintenanceWindow MakeWindow(string serverIp, string reason = "planned") => new()
    {
        ServerIp = serverIp,
        DisplayName = serverIp,
        From = DateTimeOffset.Now,
        To = DateTimeOffset.Now.AddHours(1),
        Reason = reason
    };

    [Fact]
    public async Task UpsertAsync_InsertsNewWindow()
    {
        await using var scope = _provider.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IMaintenanceRepository>();

        await repo.UpsertAsync(MakeWindow("10.0.0.1"));

        var all = await repo.LoadAllAsync();
        var window = Assert.Single(all);
        Assert.Equal("10.0.0.1", window.ServerIp);
    }

    [Fact]
    public async Task UpsertAsync_SameKeyTwice_ReplacesRatherThanDuplicates()
    {
        await using var scope = _provider.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IMaintenanceRepository>();

        await repo.UpsertAsync(MakeWindow("10.0.0.1", reason: "first"));
        await repo.UpsertAsync(MakeWindow("10.0.0.1", reason: "second"));

        var all = await repo.LoadAllAsync();
        var window = Assert.Single(all);
        Assert.Equal("second", window.Reason);
    }

    [Fact]
    public async Task RemoveAsync_RemovesByKey()
    {
        await using var scope = _provider.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IMaintenanceRepository>();
        await repo.UpsertAsync(MakeWindow("10.0.0.1"));

        await repo.RemoveAsync("10.0.0.1");

        Assert.Empty(await repo.LoadAllAsync());
    }

    [Fact]
    public async Task RemoveAsync_MissingKey_IsANoOp()
    {
        await using var scope = _provider.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IMaintenanceRepository>();

        var exception = await Record.ExceptionAsync(() => repo.RemoveAsync("no-such-key"));

        Assert.Null(exception);
    }

    [Fact]
    public async Task RemoveAllAsync_ClearsEveryWindow()
    {
        await using var scope = _provider.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IMaintenanceRepository>();
        await repo.UpsertAsync(MakeWindow("10.0.0.1"));
        await repo.UpsertAsync(new MaintenanceWindow { TargetGroup = "Core", DisplayName = "Core", From = DateTimeOffset.Now });

        await repo.RemoveAllAsync();

        Assert.Empty(await repo.LoadAllAsync());
    }

    /// <summary>
    /// Regression test for audit Finding 6.2: concurrent StartMaintenance
    /// calls for the SAME key (each on its own DbContext scope, matching how
    /// MaintenanceService actually calls this — a fresh scope per request/
    /// cycle via IServiceScopeFactory), racing against an initially empty
    /// table. Before the fix, two callers can both read "no existing row"
    /// before either has written, then both attempt an INSERT with the same
    /// WindowKey primary key — the losing SaveChangesAsync throws a raw,
    /// unhandled DbUpdateException (UNIQUE constraint failed) instead of one
    /// side cleanly winning. A single pair of concurrent calls isn't a
    /// reliable enough race window on fast local I/O (observed passing even
    /// without the fix) — 20 concurrent callers for the same key make at
    /// least one overlapping read-then-write pair highly likely, while
    /// staying fast once the fix serializes them. AppSettingsRepositoryTests
    /// exercises the same concurrency pattern for the fix this mirrors.
    /// </summary>
    [Fact]
    public async Task UpsertAsync_ConcurrentCallsForTheSameKey_DoNotThrow_AndLeaveExactlyOneRow()
    {
        const int concurrentCallers = 20;

        var tasks = Enumerable.Range(0, concurrentCallers).Select(async i =>
        {
            await using var scope = _provider.CreateAsyncScope();
            var repo = scope.ServiceProvider.GetRequiredService<IMaintenanceRepository>();
            await repo.UpsertAsync(MakeWindow("10.0.0.1", reason: $"from-{i}"));
        });

        var exception = await Record.ExceptionAsync(() => Task.WhenAll(tasks));
        Assert.Null(exception);

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyRepo = verifyScope.ServiceProvider.GetRequiredService<IMaintenanceRepository>();
        var all = await verifyRepo.LoadAllAsync();
        Assert.Single(all, w => w.ServerIp == "10.0.0.1");
    }
}
