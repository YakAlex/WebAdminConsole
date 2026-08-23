using AdminConsole.Domain.Models;
using AdminConsole.Infrastructure.Data;
using AdminConsole.Infrastructure.Monitoring;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AdminConsole.Tests.Monitoring;

/// <summary>
/// Regression test for the audit's Finding 6.1: AppLogEntries had no
/// retention policy and grew forever. Verifies AppLogRetentionJob deletes
/// only entries older than its cutoff, leaving newer ones untouched.
/// </summary>
public sealed class AppLogRetentionJobTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"AdminConsoleLogRetentionTests_{Guid.NewGuid():N}.db");
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
    public async Task RunAsync_DeletesOnlyEntriesOlderThan90Days()
    {
        await using (var scope = _provider.CreateAsyncScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<AdminConsole.Domain.Abstractions.IAppLogRepository>();
            await repo.AppendAsync(new AppLogEntry(LogSeverity.Info, "Test", "old entry", DateTimeOffset.Now.AddDays(-91)));
            await repo.AppendAsync(new AppLogEntry(LogSeverity.Info, "Test", "recent entry", DateTimeOffset.Now.AddDays(-10)));
        }

        await using var scope2 = _provider.CreateAsyncScope();
        var job = new AppLogRetentionJob(
            scope2.ServiceProvider.GetRequiredService<AdminConsole.Domain.Abstractions.IAppLogRepository>(),
            scope2.ServiceProvider.GetRequiredService<ILogger<AppLogRetentionJob>>(),
            NullMediator.Instance);

        await job.RunAsync();

        await using var verifyScope = _provider.CreateAsyncScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<AdminConsoleDbContext>();
        var remaining = await db.AppLogEntries.ToListAsync();
        Assert.Single(remaining);
        Assert.Equal("recent entry", remaining[0].Message);
    }
}

file sealed class NullMediator : MediatR.IMediator
{
    public static readonly NullMediator Instance = new();
    public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : MediatR.INotification => Task.CompletedTask;
    public Task<TResponse> Send<TResponse>(MediatR.IRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : MediatR.IRequest => throw new NotSupportedException();
    public IAsyncEnumerable<TResponse> CreateStream<TResponse>(MediatR.IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
