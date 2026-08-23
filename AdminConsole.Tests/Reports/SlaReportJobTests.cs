using AdminConsole.Domain.Events;
using AdminConsole.Domain.Models;
using AdminConsole.Infrastructure.Data;
using AdminConsole.Infrastructure.Monitoring;
using AdminConsole.Infrastructure.Reports;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AdminConsole.Tests.Reports;

/// <summary>
/// Regression test for the audit's Finding 2.1: RunWeeklyAsync had no
/// top-level try/catch, unlike BackupMonitorJob.RunAsync. This proves a
/// downstream publish failure is now logged (visible in AppLogEntries) and
/// still rethrown, so Hangfire's own retry still sees the failure.
/// </summary>
public sealed class SlaReportJobTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"AdminConsoleSlaJobTests_{Guid.NewGuid():N}.db");
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
    public async Task RunWeeklyAsync_WhenPublishThrows_LogsErrorAndRethrows()
    {
        await using var scope = _provider.CreateAsyncScope();
        var sp = scope.ServiceProvider;

        var uptime = new UptimeTrackerService(
            mediator:     NullMediator.Instance,
            scopeFactory: _provider.GetRequiredService<IServiceScopeFactory>(),
            logger:       sp.GetRequiredService<ILogger<UptimeTrackerService>>(),
            maintenance:  new MaintenanceService(
                NullMediator.Instance,
                _provider.GetRequiredService<IServiceScopeFactory>(),
                sp.GetRequiredService<ILogger<MaintenanceService>>()),
            servers:      Options.Create(new List<ServerEntry>()),
            settings:     Options.Create(new AdminConsole.Infrastructure.Configuration.MonitoringSettings()));
        await uptime.StartAsync(default);

        var slaReportService = new SlaReportService(Options.Create(new List<ServerEntry>()), uptime);
        var mediator = new ThrowOnceMediator();

        var job = new SlaReportJob(mediator, slaReportService, sp.GetRequiredService<ILogger<SlaReportJob>>());

        await Assert.ThrowsAsync<InvalidOperationException>(() => job.RunWeeklyAsync());

        Assert.Contains(mediator.Published, n => n is AppLogEntryOccurred e && e.Entry.Severity == LogSeverity.Error);
    }
}

/// <summary>Throws on the first Publish call (simulating a downstream handler failure), records every call after that.</summary>
file sealed class ThrowOnceMediator : MediatR.IMediator
{
    public List<object> Published { get; } = new();
    private bool _thrown;

    public Task Publish(object notification, CancellationToken cancellationToken = default)
    {
        if (!_thrown)
        {
            _thrown = true;
            throw new InvalidOperationException("simulated downstream handler failure");
        }
        Published.Add(notification);
        return Task.CompletedTask;
    }

    public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
        where TNotification : MediatR.INotification
        => Publish((object)notification!, cancellationToken);

    public Task<TResponse> Send<TResponse>(MediatR.IRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : MediatR.IRequest => throw new NotSupportedException();
    public IAsyncEnumerable<TResponse> CreateStream<TResponse>(MediatR.IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
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
