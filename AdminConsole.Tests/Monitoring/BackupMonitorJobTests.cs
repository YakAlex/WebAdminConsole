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
/// T4.15 — юніт-тести анти-флапінгу BackupMonitorJob. Женемо джобу через
/// кілька реальних циклів проти файлів у тимчасовій теці (BackupCheckEvaluator
/// не має інтерфейсу для мокання — Stage A/B зроблений як реальна файлова
/// перевірка, тому це найпряміший спосіб перевірити саме анти-флапінг-логіку
/// джоби, а не сам файловий Stage A/B, який уже покритий власною логікою
/// в BackupCheckEvaluator).
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
        services.AddAdminConsoleDb($"Data Source={_dbPath}"); // вже реєструє IMaintenanceRepository/IBackupStateRepository/IAppSettingsRepository (Scoped)
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
    /// Створює свіжий job-екземпляр і одразу проганяє RunAsync В МЕЖАХ ТОГО
    /// САМОГО DI-scope — так само, як реально робить Hangfire (новий scope
    /// на кожен запуск, з власним AdminConsoleDbContext, що живе рівно на
    /// час одного виконання). Scope НЕ можна закрити до завершення RunAsync,
    /// інакше репозиторії всередині job лишаються з disposed DbContext.
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
            MinSamplesForBaseline   = 100 // вимикаємо SizeWarning-гілку — тестуємо лише вік/анти-флапінг
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
            File.SetLastWriteTime(file, DateTime.Now.AddHours(-48)); // старше MaxAgeHoursFull=26
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
        // Цикл 1: свіжий бекап → підтверджено Ok (перше підтвердження, миттєво).
        WriteFreshBackupFile();
        await RunJobOnceAsync(minConsecutiveForAlert: 2);
        Assert.Equal(BackupOutcome.Ok, (await GetPersistedStateAsync()).Outcome);

        // Цикл 2: файл застарів → "сирий" Stale, але лише 1 раз поспіль
        // (MinConsecutiveForAlert=2) — підтверджений стан МАЄ лишитись Ok.
        MakeAllBackupFilesStale();
        await RunJobOnceAsync(minConsecutiveForAlert: 2);

        var afterOneStaleReading = await GetPersistedStateAsync();
        Assert.Equal(BackupOutcome.Ok, afterOneStaleReading.Outcome);
        Assert.Equal(1, afterOneStaleReading.ConsecutiveBadCount);

        // Цикл 3: другий Stale поспіль → досягнуто порогу → підтверджений
        // перехід Ok → Stale, лічильник скинуто.
        await RunJobOnceAsync(minConsecutiveForAlert: 2);

        var afterTwoStaleReadings = await GetPersistedStateAsync();
        Assert.Equal(BackupOutcome.Stale, afterTwoStaleReadings.Outcome);
        Assert.Equal(0, afterTwoStaleReadings.ConsecutiveBadCount);
    }
}

/// <summary>Мінімальний no-op IMediator для тестів, де публікація подій не перевіряється — лише кінцевий персистентний стан.</summary>
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
