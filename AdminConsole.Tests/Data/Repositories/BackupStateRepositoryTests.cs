using AdminConsole.Domain.Abstractions;
using AdminConsole.Domain.Models;
using AdminConsole.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AdminConsole.Tests.Data.Repositories;

public sealed class BackupStateRepositoryTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"AdminConsoleBackupStateRepoTests_{Guid.NewGuid():N}.db");
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

    private static BackupCheckState MakeState(
        string name, BackupOutcome outcome = BackupOutcome.Ok, IEnumerable<BackupSample>? history = null) => new()
    {
        Name = name,
        Host = name,
        Kind = BackupKind.Full,
        Outcome = outcome,
        History = history?.ToList() ?? []
    };

    [Fact]
    public async Task UpsertAsync_InsertsNewState_WithHistory()
    {
        await using var scope = _provider.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IBackupStateRepository>();

        await repo.UpsertAsync(MakeState("Server1", history:
        [
            new BackupSample { ObservedAt = DateTimeOffset.Now, SizeBytes = 1000 }
        ]));

        var all = await repo.LoadAllAsync();
        var state = Assert.Single(all);
        Assert.Equal("Server1", state.Name);
        Assert.Single(state.History);
    }

    [Fact]
    public async Task UpsertAsync_ExistingKey_UpdatesFields_AndFullyReplacesHistory()
    {
        await using var scope = _provider.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IBackupStateRepository>();

        await repo.UpsertAsync(MakeState("Server1", outcome: BackupOutcome.Ok, history:
        [
            new BackupSample { ObservedAt = DateTimeOffset.Now.AddDays(-2), SizeBytes = 1000 },
            new BackupSample { ObservedAt = DateTimeOffset.Now.AddDays(-1), SizeBytes = 1100 }
        ]));

        // Same key (Name+Kind) — must update in place, and the new History
        // list must entirely replace the old one, not append to it.
        await repo.UpsertAsync(MakeState("Server1", outcome: BackupOutcome.Stale, history:
        [
            new BackupSample { ObservedAt = DateTimeOffset.Now, SizeBytes = 2000 }
        ]));

        var all = await repo.LoadAllAsync();
        var state = Assert.Single(all);
        Assert.Equal(BackupOutcome.Stale, state.Outcome);
        var sample = Assert.Single(state.History);
        Assert.Equal(2000, sample.SizeBytes);
    }

    [Fact]
    public async Task DeleteWhereKeyNotInAsync_RemovesOnlyStaleKeys()
    {
        await using var scope = _provider.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IBackupStateRepository>();
        await repo.UpsertAsync(MakeState("Server1"));
        await repo.UpsertAsync(MakeState("Server2"));

        var removedCount = await repo.DeleteWhereKeyNotInAsync(new HashSet<string> { "Server1|Full" });

        Assert.Equal(1, removedCount);
        var remaining = Assert.Single(await repo.LoadAllAsync());
        Assert.Equal("Server1", remaining.Name);
    }

    [Fact]
    public async Task DeleteWhereKeyNotInAsync_AllKeysValid_RemovesNothing()
    {
        await using var scope = _provider.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IBackupStateRepository>();
        await repo.UpsertAsync(MakeState("Server1"));

        var removedCount = await repo.DeleteWhereKeyNotInAsync(new HashSet<string> { "Server1|Full" });

        Assert.Equal(0, removedCount);
        Assert.Single(await repo.LoadAllAsync());
    }
}
