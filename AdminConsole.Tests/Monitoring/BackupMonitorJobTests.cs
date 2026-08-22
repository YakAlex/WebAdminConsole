using AdminConsole.Domain.Abstractions;
using AdminConsole.Domain.Models;
using AdminConsole.Infrastructure.Data;
using AdminConsole.Infrastructure.Monitoring;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AdminConsole.Tests.Monitoring;

/// <summary>
/// T4.15 — unit tests for BackupMonitorJob's anti-flapping logic. Runs the
/// job through several real cycles against files in a temp directory
/// (BackupCheckEvaluator has no mockable interface — Stage A/B is
/// implemented as a real file check, so this is the most direct way to test
/// the job's anti-flapping logic specifically, as opposed to the file Stage
/// A/B itself, which is already covered by its own tests in
/// BackupCheckEvaluator).
/// </summary>
public sealed class BackupMonitorJobTests : IAsyncLifetime
{
    private readonly string _rootDir = Path.Combine(Path.GetTempPath(), "AdminConsoleBackupJobTests_" + Guid.NewGuid());
    private string _backupDir = null!;
    private string _dbPath    = null!;
    private ServiceProvider _provider = null!;

    private const string ServerName = "Server1";
    private const string FilePattern = "backup_*.bak";

    public async Task InitializeAsync()
    {
        _backupDir = Path.Combine(_rootDir, "backups");
        Directory.CreateDirectory(_backupDir);
        _dbPath = Path.Combine(_rootDir, "test.db");

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAdminConsoleDb($"Data Source={_dbPath}"); // already registers IMaintenanceRepository/IBackupStateRepository/IAppSettingsRepository (Scoped)
        services.AddSingleton<BackupCheckEvaluator>();

        _provider = services.BuildServiceProvider();

        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AdminConsoleDbContext>();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync()
    {
        _provider.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_rootDir))
            Directory.Delete(_rootDir, recursive: true);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Creates a fresh job instance and immediately runs RunAsync WITHIN THAT
    /// SAME DI scope — the same way Hangfire actually does it (a new scope
    /// per run, with its own AdminConsoleDbContext that lives for exactly the
    /// duration of one execution). The scope must NOT be closed before
    /// RunAsync finishes, or the repositories inside the job end up with a
    /// disposed DbContext.
    /// </summary>
    private async Task RunJobOnceAsync(int minConsecutiveForAlert)
    {
        await using var scope = _provider.CreateAsyncScope();
        var sp = scope.ServiceProvider;

        var definition = new BackupCheckDefinition
        {
            Name                    = ServerName,
            Host                    = ServerName,
            Path                    = _backupDir,
            FullPattern             = FilePattern,
            DiffPattern             = string.Empty,
            MaxAgeHoursFull         = 26,
            MinConsecutiveForAlert  = minConsecutiveForAlert,
            MinSamplesForBaseline   = 100 // disables the SizeWarning branch — we're only testing age/anti-flapping
        };

        var job = new BackupMonitorJob(
            mediator:      NullMediator.Instance,
            logger:        sp.GetRequiredService<ILogger<BackupMonitorJob>>(),
            settings:      Options.Create(new AdminConsole.Infrastructure.Configuration.MonitoringSettings()),
            backupChecks:  Options.Create(new List<BackupCheckDefinition> { definition }),
            servers:       Options.Create(new List<ServerEntry>
                { new() { Name = ServerName, IP = "10.0.0.1", Group = "Test" } }),
            maintenance:   new MaintenanceService(
                NullMediator.Instance,
                _provider.GetRequiredService<IServiceScopeFactory>(),
                sp.GetRequiredService<ILogger<MaintenanceService>>()),
            evaluator:     sp.GetRequiredService<BackupCheckEvaluator>(),
            repository:    sp.GetRequiredService<IBackupStateRepository>(),
            appSettings:   sp.GetRequiredService<IAppSettingsRepository>());

        await job.RunAsync();
    }

    private void WriteFreshBackupFile()
    {
        var path = Path.Combine(_backupDir, $"backup_{Guid.NewGuid():N}.bak");
        File.WriteAllText(path, "data");
        File.SetLastWriteTime(path, DateTime.Now);
    }

    private void MakeAllBackupFilesStale()
    {
        foreach (var file in Directory.EnumerateFiles(_backupDir, FilePattern))
            File.SetLastWriteTime(file, DateTime.Now.AddHours(-48)); // older than MaxAgeHoursFull=26
    }

    private async Task<BackupCheckState> GetPersistedStateAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AdminConsoleDbContext>();
        return await db.BackupCheckStates.Include(s => s.History)
            .SingleAsync(s => s.Name == ServerName && s.Kind == BackupKind.Full);
    }

    [Fact]
    public async Task FirstRun_ConfirmsImmediately_WithoutWaitingForMinConsecutiveForAlert()
    {
        WriteFreshBackupFile();

        await RunJobOnceAsync(minConsecutiveForAlert: 2);

        var state = await GetPersistedStateAsync();
        Assert.Equal(BackupOutcome.Ok, state.Outcome);
        Assert.Equal(0, state.ConsecutiveBadCount);
    }

    [Fact]
    public async Task SingleStaleReading_DoesNotFlipConfirmedOutcome_AntiFlapping()
    {
        // Cycle 1: fresh backup → confirmed Ok (first confirmation, instant).
        WriteFreshBackupFile();
        await RunJobOnceAsync(minConsecutiveForAlert: 2);
        Assert.Equal(BackupOutcome.Ok, (await GetPersistedStateAsync()).Outcome);

        // Cycle 2: file went stale → a "raw" Stale reading, but only once in a
        // row (MinConsecutiveForAlert=2) — the confirmed state MUST stay Ok.
        MakeAllBackupFilesStale();
        await RunJobOnceAsync(minConsecutiveForAlert: 2);

        var afterOneStaleReading = await GetPersistedStateAsync();
        Assert.Equal(BackupOutcome.Ok, afterOneStaleReading.Outcome);
        Assert.Equal(1, afterOneStaleReading.ConsecutiveBadCount);

        // Cycle 3: second Stale reading in a row → threshold reached →
        // confirmed transition Ok → Stale, counter reset.
        await RunJobOnceAsync(minConsecutiveForAlert: 2);

        var afterTwoStaleReadings = await GetPersistedStateAsync();
        Assert.Equal(BackupOutcome.Stale, afterTwoStaleReadings.Outcome);
        Assert.Equal(0, afterTwoStaleReadings.ConsecutiveBadCount);
    }
}

/// <summary>Minimal no-op IMediator for tests where event publication isn't checked — only the final persisted state.</summary>
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
