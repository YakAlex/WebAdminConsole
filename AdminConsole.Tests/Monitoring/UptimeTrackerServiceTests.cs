using AdminConsole.Domain.Events;
using AdminConsole.Domain.Models;
using AdminConsole.Infrastructure.Configuration;
using AdminConsole.Infrastructure.Data;
using AdminConsole.Infrastructure.Monitoring;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AdminConsole.Tests.Monitoring;

/// <summary>
/// Regression test for the audit's Finding 7.1: an unhandled exception in
/// UptimeTrackerService.Handle(PingBatchResultOccurred) used to propagate
/// all the way back into PingMonitorService's loop and permanently stop it.
/// This test proves Handle() now swallows a DB failure internally instead.
/// </summary>
public sealed class UptimeTrackerServiceTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"AdminConsoleUptimeTests_{Guid.NewGuid():N}.db");
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
    public async Task Handle_PingBatchResultOccurred_WhenDbWriteFails_DoesNotThrow_AndLogsError()
    {
        const string ip = "10.0.0.1";
        var servers = new List<ServerEntry> { new() { Name = "Server1", IP = ip, Group = "Core" } };
        var mediator = new RecordingMediator();

        await using var scope = _provider.CreateAsyncScope();
        var sp = scope.ServiceProvider;

        var uptime = new UptimeTrackerService(
            mediator:     mediator,
            scopeFactory: _provider.GetRequiredService<IServiceScopeFactory>(),
            logger:       sp.GetRequiredService<ILogger<UptimeTrackerService>>(),
            maintenance:  new MaintenanceService(
                mediator,
                _provider.GetRequiredService<IServiceScopeFactory>(),
                sp.GetRequiredService<ILogger<MaintenanceService>>()),
            servers:      Options.Create(servers),
            // MinIncidentDurationSeconds = 0 disables the "must stay down N
            // seconds before it counts" filter, so the second Offline ping
            // below immediately promotes to a DowntimeRecord write.
            settings:     Options.Create(new MonitoringSettings { MinIncidentDurationSeconds = 0 }));

        await uptime.StartAsync(default);

        // Warm up _lastStatus[ip] = Online (a fresh server's first-ever ping
        // is treated as "reconciling", not a real transition — it does no DB
        // write, so the table is still intact for this call).
        await uptime.Handle(new PingBatchResultOccurred(new PingBatchPayload(
            Results: [new PingResult("Server1", ip, "Core", PingStatus.Online, LatencyMs: 5, LastChecked: DateTimeOffset.Now)],
            CycleCompletedAt: DateTimeOffset.Now)), default);

        // First Offline ping: creates a _pendingOffline entry — still no DB write yet.
        await uptime.Handle(new PingBatchResultOccurred(new PingBatchPayload(
            Results: [new PingResult("Server1", ip, "Core", PingStatus.Offline, LatencyMs: null, LastChecked: DateTimeOffset.Now)],
            CycleCompletedAt: DateTimeOffset.Now)), default);

        // Break the schema now, so the NEXT write (triggered by the next
        // Handle call below) genuinely fails with a real SqliteException,
        // simulating a persistent SaveChangesAsync failure.
        await using (var breakScope = _provider.CreateAsyncScope())
        {
            var db = breakScope.ServiceProvider.GetRequiredService<AdminConsoleDbContext>();
            await db.Database.ExecuteSqlRawAsync("DROP TABLE DowntimeRecords");
        }

        // Second Offline ping: promotes the pending entry to a DowntimeRecord
        // and attempts the now-broken DB write. Must NOT throw.
        var exception = await Record.ExceptionAsync(() => uptime.Handle(new PingBatchResultOccurred(new PingBatchPayload(
            Results: [new PingResult("Server1", ip, "Core", PingStatus.Offline, LatencyMs: null, LastChecked: DateTimeOffset.Now)],
            CycleCompletedAt: DateTimeOffset.Now)), default));

        Assert.Null(exception);
        Assert.Contains(mediator.Published, n => n is AppLogEntryOccurred e && e.Entry.Severity == LogSeverity.Error);
    }
}

/// <summary>Records every published notification instead of no-oping — lets a test assert on what was logged.</summary>
file sealed class RecordingMediator : MediatR.IMediator
{
    public List<object> Published { get; } = new();

    public Task Publish(object notification, CancellationToken cancellationToken = default)
    {
        Published.Add(notification);
        return Task.CompletedTask;
    }

    public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
        where TNotification : MediatR.INotification
    {
        Published.Add(notification!);
        return Task.CompletedTask;
    }

    public Task<TResponse> Send<TResponse>(MediatR.IRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : MediatR.IRequest => throw new NotSupportedException();
    public IAsyncEnumerable<TResponse> CreateStream<TResponse>(MediatR.IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
