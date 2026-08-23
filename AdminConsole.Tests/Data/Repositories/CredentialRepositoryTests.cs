using AdminConsole.Domain.Abstractions;
using AdminConsole.Domain.Models;
using AdminConsole.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AdminConsole.Tests.Data.Repositories;

public sealed class CredentialRepositoryTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"AdminConsoleCredentialRepoTests_{Guid.NewGuid():N}.db");
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
    public async Task GetAsync_MissingTarget_ReturnsNull()
    {
        await using var scope = _provider.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<ICredentialRepository>();

        Assert.Null(await repo.GetAsync("Zabbix"));
    }

    [Fact]
    public async Task UpsertAsync_InsertsNewCredential()
    {
        await using var scope = _provider.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<ICredentialRepository>();

        await repo.UpsertAsync(new StoredCredential { Target = "Zabbix", Username = "admin", ProtectedSecret = [1, 2, 3] });

        var stored = await repo.GetAsync("Zabbix");
        Assert.NotNull(stored);
        Assert.Equal("admin", stored!.Username);
        Assert.Equal(new byte[] { 1, 2, 3 }, stored.ProtectedSecret);
    }

    [Fact]
    public async Task UpsertAsync_SameTarget_UpdatesInPlace()
    {
        await using var scope = _provider.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<ICredentialRepository>();
        await repo.UpsertAsync(new StoredCredential { Target = "Zabbix", Username = "admin", ProtectedSecret = [1] });

        await repo.UpsertAsync(new StoredCredential { Target = "Zabbix", Username = "new-admin", ProtectedSecret = [9, 9] });

        var stored = await repo.GetAsync("Zabbix");
        Assert.Equal("new-admin", stored!.Username);
        Assert.Equal(new byte[] { 9, 9 }, stored.ProtectedSecret);
    }

    [Fact]
    public async Task UpsertAsync_DifferentTargets_DoNotCollide()
    {
        await using var scope = _provider.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<ICredentialRepository>();

        await repo.UpsertAsync(new StoredCredential { Target = "Zabbix", Username = "z", ProtectedSecret = [1] });
        await repo.UpsertAsync(new StoredCredential { Target = "Telegram", Username = null, ProtectedSecret = [2] });

        Assert.Equal("z", (await repo.GetAsync("Zabbix"))!.Username);
        Assert.Null((await repo.GetAsync("Telegram"))!.Username);
    }

    [Fact]
    public async Task DeleteAsync_RemovesCredential()
    {
        await using var scope = _provider.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<ICredentialRepository>();
        await repo.UpsertAsync(new StoredCredential { Target = "Zabbix", Username = "admin", ProtectedSecret = [1] });

        await repo.DeleteAsync("Zabbix");

        Assert.Null(await repo.GetAsync("Zabbix"));
    }

    [Fact]
    public async Task DeleteAsync_MissingTarget_IsANoOp()
    {
        await using var scope = _provider.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<ICredentialRepository>();

        var exception = await Record.ExceptionAsync(() => repo.DeleteAsync("no-such-target"));

        Assert.Null(exception);
    }
}
