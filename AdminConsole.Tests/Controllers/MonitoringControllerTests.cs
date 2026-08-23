using AdminConsole.Api.Controllers;
using AdminConsole.Domain.Abstractions;
using AdminConsole.Domain.Events;
using AdminConsole.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AdminConsole.Tests.Controllers;

public sealed class MonitoringControllerTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"AdminConsoleMonitoringControllerTests_{Guid.NewGuid():N}.db");
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

    private (MonitoringController Controller, RecordingMediator Mediator, IAppSettingsRepository Repo) CreateSut()
    {
        var scope = _provider.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IAppSettingsRepository>();
        var mediator = new RecordingMediator();
        return (new MonitoringController(repo, mediator), mediator, repo);
    }

    [Fact]
    public async Task UpdateToggles_SeverityOnlyChange_PublishesMonitoringToggledOccurredForZabbix()
    {
        var (controller, mediator, repo) = CreateSut();
        var current = await repo.GetAsync(); // seeds the default row (ZabbixMinSeverity = 4)

        await controller.UpdateToggles(new UpdateMonitoringTogglesRequest(
            current.RdpMonitoringEnabled, current.ZabbixMonitoringEnabled, current.BackupMonitoringEnabled,
            ZabbixMinSeverity: 2), CancellationToken.None);

        var zabbixEvents = mediator.Published.OfType<MonitoringToggledOccurred>()
            .Where(e => e.Service == MonitoredService.Zabbix).ToList();
        Assert.Single(zabbixEvents);
    }

    [Fact]
    public async Task UpdateToggles_NothingChanged_PublishesNoEvents()
    {
        var (controller, mediator, repo) = CreateSut();
        var current = await repo.GetAsync();

        await controller.UpdateToggles(new UpdateMonitoringTogglesRequest(
            current.RdpMonitoringEnabled, current.ZabbixMonitoringEnabled, current.BackupMonitoringEnabled,
            current.ZabbixMinSeverity), CancellationToken.None);

        Assert.Empty(mediator.Published);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(-1)]
    [InlineData(999)]
    public async Task UpdateToggles_SeverityOutOfRange_ReturnsBadRequestAndDoesNotPersist(int outOfRangeSeverity)
    {
        var (controller, _, repo) = CreateSut();
        var current = await repo.GetAsync(); // ZabbixMinSeverity = 4

        var result = await controller.UpdateToggles(new UpdateMonitoringTogglesRequest(
            current.RdpMonitoringEnabled, current.ZabbixMonitoringEnabled, current.BackupMonitoringEnabled,
            outOfRangeSeverity), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);

        var reloaded = await repo.GetAsync();
        Assert.Equal(4, reloaded.ZabbixMinSeverity);
    }
}
