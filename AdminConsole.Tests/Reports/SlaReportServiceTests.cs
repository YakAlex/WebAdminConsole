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
/// T4.15 — юніт-тести SLA-алгоритму. Дані сідуються напряму через
/// IDowntimeRepository (в обхід живого ping-конвеєра UptimeTrackerService),
/// потім свіжий UptimeTrackerService.StartAsync() довантажує їх у _records —
/// той самий шлях, яким сервіс читає з БД при реальному старті. Це дає
/// повний контроль над FellAt/RecoveredAt для перевірки точної математики
/// (Uptime%, MTTR, maintenance-виключення), без залежності від живого часу
/// виконання тесту.
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

        await uptime.StartAsync(ct); // await'ить LoadFromDbAsync ДО повернення (T4.3)
        return uptime;
    }

    [Fact]
    public async Task Generate_ComputesUptimePercent_AndExcludesMaintenanceFromMainStats()
    {
        var from = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var to   = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero); // 24-годинний період

        await using (var scope = _provider.CreateAsyncScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<AdminConsole.Domain.Abstractions.IDowntimeRepository>();

            // Server1: реальний інцидент — 30 хв простою.
            await repo.UpsertAsync(new DowntimeRecord
            {
                ServerName  = "Server1",
                ServerIp    = "10.0.0.1",
                ServerGroup = "Core",
                FellAt      = from.AddHours(1),
                RecoveredAt = from.AddHours(1).AddMinutes(30),
                ClosedByMaintenance = false
            });

            // Server2: інцидент, закритий через Maintenance — 2 години,
            // НЕ повинен впливати на UptimePercent/IncidentCount Server2.
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
        Assert.Equal(0, server2.IncidentCount); // maintenance-інцидент не рахується як звичайний
        Assert.Equal(TimeSpan.Zero, server2.DowntimeInPeriod);
        Assert.Equal(TimeSpan.FromHours(2), server2.MaintenanceDowntimeInPeriod);
        Assert.Equal(100.0, server2.UptimePercent); // maintenance не псує показник
        Assert.Null(server2.Mttr); // MTTR лічить лише реальні відновлення, не maintenance-закриття

        var appendixEntry = Assert.Single(report.MaintenanceAppendix);
        Assert.Equal("Server2", appendixEntry.ServerName);
    }

    [Fact]
    public async Task GetFleetAvailabilityPercent_UnionsOverlappingIncidents_AcrossServers()
    {
        var from = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var to   = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero); // 10-годинний період

        await using (var scope = _provider.CreateAsyncScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<AdminConsole.Domain.Abstractions.IDowntimeRepository>();

            // Два сервери падають ОДНОЧАСНО й частково перекриваються:
            // Server1: [1h, 3h], Server2: [2h, 4h] → об'єднання = [1h, 4h] = 3 години,
            // а НЕ 2+2=4 години (без union-логіки помилково подвоїло б простій).
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
