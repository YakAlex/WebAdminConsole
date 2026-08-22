using AdminConsole.Domain.Models;
using AdminConsole.Domain.Models.Reports;
using AdminConsole.Infrastructure.Data;
using AdminConsole.Infrastructure.Monitoring;
using AdminConsole.Infrastructure.Reports;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AdminConsole.Tests.Reports;

/// <summary>
/// T4.15 — unit tests for the SLA algorithm. Data is seeded directly via
/// IDowntimeRepository (bypassing UptimeTrackerService's live ping
/// pipeline), then a fresh UptimeTrackerService.StartAsync() loads it into
/// _records — the same path the service uses to read from the DB on a real
/// startup. This gives full control over FellAt/RecoveredAt to verify the
/// exact math (Uptime%, MTTR, maintenance exclusions), independent of the
/// test's actual wall-clock run time.
/// </summary>
public sealed class SlaReportServiceTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"AdminConsoleSlaTests_{Guid.NewGuid():N}.db");
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

    private async Task<UptimeTrackerService> CreateLoadedUptimeTrackerAsync(
        IReadOnlyList<ServerEntry> servers, CancellationToken ct = default)
    {
        await using var scope = _provider.CreateAsyncScope();
        var sp = scope.ServiceProvider;

        var uptime = new UptimeTrackerService(
            mediator:     NullMediator.Instance,
            scopeFactory: _provider.GetRequiredService<IServiceScopeFactory>(),
            logger:       sp.GetRequiredService<ILogger<UptimeTrackerService>>(),
            maintenance: new MaintenanceService(
                NullMediator.Instance,
                _provider.GetRequiredService<IServiceScopeFactory>(),
                sp.GetRequiredService<ILogger<MaintenanceService>>()),
            servers:  Options.Create(servers.ToList()),
            settings: Options.Create(new AdminConsole.Infrastructure.Configuration.MonitoringSettings()));

        await uptime.StartAsync(ct); // awaits LoadFromDbAsync BEFORE returning (T4.3)
        return uptime;
    }

    [Fact]
    public async Task Generate_ComputesUptimePercent_AndExcludesMaintenanceFromMainStats()
    {
        var from = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var to   = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero); // 24-hour period

        await using (var scope = _provider.CreateAsyncScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<AdminConsole.Domain.Abstractions.IDowntimeRepository>();

            // Server1: a real incident — 30 minutes of downtime.
            await repo.UpsertAsync(new DowntimeRecord
            {
                ServerName  = "Server1",
                ServerIp    = "10.0.0.1",
                ServerGroup = "Core",
                FellAt      = from.AddHours(1),
                RecoveredAt = from.AddHours(1).AddMinutes(30),
                ClosedByMaintenance = false
            });

            // Server2: an incident closed via Maintenance — 2 hours,
            // must NOT affect Server2's UptimePercent/IncidentCount.
            await repo.UpsertAsync(new DowntimeRecord
            {
                ServerName  = "Server2",
                ServerIp    = "10.0.0.2",
                ServerGroup = "Core",
                FellAt      = from.AddHours(5),
                RecoveredAt = from.AddHours(7),
                ClosedByMaintenance = true
            });
        }

        var servers = new List<ServerEntry>
        {
            new() { Name = "Server1", IP = "10.0.0.1", Group = "Core" },
            new() { Name = "Server2", IP = "10.0.0.2", Group = "Core" }
        };

        var uptime = await CreateLoadedUptimeTrackerAsync(servers);
        var sla = new SlaReportService(Options.Create(servers), uptime);

        var report = sla.Generate(new SlaReportRequest { From = from, To = to });

        var server1 = Assert.Single(report.Servers, s => s.ServerName == "Server1");
        Assert.Equal(1, server1.IncidentCount);
        Assert.Equal(TimeSpan.FromMinutes(30), server1.DowntimeInPeriod);
        Assert.Equal(TimeSpan.Zero, server1.MaintenanceDowntimeInPeriod);
        // (24h - 30min) / 24h * 100 = 97.91666...%
        Assert.Equal(97.9167, server1.UptimePercent, precision: 3);
        Assert.Equal(TimeSpan.FromMinutes(30), server1.Mttr);

        var server2 = Assert.Single(report.Servers, s => s.ServerName == "Server2");
        Assert.Equal(0, server2.IncidentCount); // a maintenance incident doesn't count as a regular one
        Assert.Equal(TimeSpan.Zero, server2.DowntimeInPeriod);
        Assert.Equal(TimeSpan.FromHours(2), server2.MaintenanceDowntimeInPeriod);
        Assert.Equal(100.0, server2.UptimePercent); // maintenance doesn't hurt the metric
        Assert.Null(server2.Mttr); // MTTR only counts real recoveries, not maintenance closures

        var appendixEntry = Assert.Single(report.MaintenanceAppendix);
        Assert.Equal("Server2", appendixEntry.ServerName);
    }

    [Fact]
    public async Task GetFleetAvailabilityPercent_UnionsOverlappingIncidents_AcrossServers()
    {
        var from = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var to   = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero); // 10-hour period

        await using (var scope = _provider.CreateAsyncScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<AdminConsole.Domain.Abstractions.IDowntimeRepository>();

            // Two servers go down SIMULTANEOUSLY and partially overlap:
            // Server1: [1h, 3h], Server2: [2h, 4h] → union = [1h, 4h] = 3 hours,
            // NOT 2+2=4 hours (without union logic this would double-count the downtime).
            await repo.UpsertAsync(new DowntimeRecord
            {
                ServerName = "Server1", ServerIp = "10.0.0.1", ServerGroup = "Core",
                FellAt = from.AddHours(1), RecoveredAt = from.AddHours(3)
            });
            await repo.UpsertAsync(new DowntimeRecord
            {
                ServerName = "Server2", ServerIp = "10.0.0.2", ServerGroup = "Core",
                FellAt = from.AddHours(2), RecoveredAt = from.AddHours(4)
            });
        }

        var servers = new List<ServerEntry>
        {
            new() { Name = "Server1", IP = "10.0.0.1", Group = "Core" },
            new() { Name = "Server2", IP = "10.0.0.2", Group = "Core" }
        };

        var uptime = await CreateLoadedUptimeTrackerAsync(servers);
        var sla = new SlaReportService(Options.Create(servers), uptime);

        var fleetAvailability = sla.GetFleetAvailabilityPercent(from, to);

        // (10h - 3h) / 10h * 100 = 70%
        Assert.Equal(70.0, fleetAvailability, precision: 3);
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
