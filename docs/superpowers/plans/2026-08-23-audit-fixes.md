# Audit Fixes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix the six audit findings the user has already reviewed and approved a specific solution for (`docs/audit/2026-08-23-full-project-audit-report.md`, on branch `worktree-full-project-audit`): 7.1, 1.1, 2.1, 6.1+5.1, 3.1, 8.1.

**Architecture:** Six independent, self-contained tasks — no task depends on another's code. Each task fixes exactly the approved variant, nothing more (no speculative extensions). C# tasks follow this repo's established TDD/test patterns (xUnit, `IAsyncLifetime` + a real temp SQLite DB, a `file sealed class` `IMediator` test double per test file — see `AdminConsole.Tests/Monitoring/BackupMonitorJobTests.cs` for the exact precedent every C# task below follows). The one frontend task has no automated test to write against — this repo's `adminconsole-web/package.json` defines no `test` script and no Vitest/Jest config — so that task is verified manually through the dev server instead.

**Tech Stack:** ASP.NET Core 8 (net8.0-windows), EF Core + SQLite, Hangfire, MediatR, xUnit; React 19 + TypeScript + Vite (Task 6 only).

**Spec:** `docs/audit/2026-08-23-full-project-audit-report.md` (Findings 7.1, 1.1, 2.1, 6.1, 5.1, 3.1, 8.1) + the user's own approved-variant message in this conversation, which this plan argues from directly (no separate design doc was written — the user specified the exact approach for each finding themselves).

## Global Constraints

- Change only what each finding requires — no unrelated refactoring, no renaming, no "while I'm here" cleanup (matches this session's own established norm across every prior fix commit).
- Every C# task must leave `dotnet build AdminConsole.sln` at 0 warnings/0 errors and `dotnet test AdminConsole.Tests/AdminConsole.Tests.csproj` fully green before its commit.
- Task 6 (frontend) must leave `npx tsc --noEmit` and `npx oxlint` clean before its commit (run from `adminconsole-web/`).
- This work happens on a **new** branch/worktree, separate from `worktree-full-project-audit` (the report-only branch, which the user asked to leave untouched) and separate from `main`.

---

### Task 1: Finding 7.1 — `UptimeTrackerService`'s notification handlers must never crash their publisher

**Files:**
- Modify: `AdminConsole.Infrastructure/Monitoring/UptimeTrackerService.cs`
- Test: Create `AdminConsole.Tests/Monitoring/UptimeTrackerServiceTests.cs`

**Interfaces:**
- Consumes: nothing from other tasks.
- Produces: nothing other tasks rely on.

- [ ] **Step 1: Write the failing test**

Create `AdminConsole.Tests/Monitoring/UptimeTrackerServiceTests.cs`:

```csharp
using AdminConsole.Domain.Abstractions;
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
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test AdminConsole.Tests/AdminConsole.Tests.csproj --filter FullyQualifiedName~UptimeTrackerServiceTests -v n`
Expected: FAIL — the assertion `Assert.Null(exception)` fails because `Handle` currently rethrows the `DbUpdateException` from the dropped table.

- [ ] **Step 3: Implement the fix**

In `AdminConsole.Infrastructure/Monitoring/UptimeTrackerService.cs`, wrap the body of `Handle(PingBatchResultOccurred notification, CancellationToken ct)` in a try/catch. Change:

```csharp
    public async Task Handle(PingBatchResultOccurred notification, CancellationToken ct)
    {
        bool changed = false;
```

to:

```csharp
    // Bug fix (2026-08-23, audit Finding 7.1): this handler used to have no
    // exception protection at all — an unhandled DB failure here propagated
    // back through PingMonitorService.PingServersAsync into its loop guard,
    // which cancels BOTH the main and recovery ping loops together by
    // design (a subscriber crashing its publisher). In a pub/sub MediatR
    // handler, a subscriber must never take down the publisher this way —
    // one missed write to the downtime table is a much smaller problem than
    // permanently halting all ping/uptime monitoring until a service
    // restart. OperationCanceledException still propagates (normal
    // shutdown); anything else is logged and swallowed.
    public async Task Handle(PingBatchResultOccurred notification, CancellationToken ct)
    {
        try
        {
            await HandlePingBatchResultAsync(notification, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "UptimeTrackerService: Handle(PingBatchResultOccurred) failed.");
            try
            {
                await mediator.Publish(AppLogEntryOccurred.Error(LogSource,
                    $"Uptime tracker: failed to process a ping batch — {ex.GetType().Name}: {ex.Message}."), CancellationToken.None);
            }
            catch { /* best effort — the ILogger call above already recorded what matters */ }
        }
    }

    private async Task HandlePingBatchResultAsync(PingBatchResultOccurred notification, CancellationToken ct)
    {
        bool changed = false;
```

The rest of the original method body (everything from `var touched = new List<DowntimeRecord>();` through the final `await PublishSnapshotAsync(ct);`) moves unchanged into `HandlePingBatchResultAsync` — only the method's opening two lines and its closing brace placement change; no logic inside is touched.

Apply the identical pattern to `HandleMaintenanceStartedAsync` — rename the existing method to `HandleMaintenanceStartedInternalAsync` (body unchanged) and add a wrapper:

```csharp
    // Bug fix (2026-08-23, audit Finding 7.1): same reasoning as
    // HandlePingBatchResultAsync above — this is called from
    // Handle(MaintenanceChangedOccurred), which had the identical gap.
    private async Task HandleMaintenanceStartedAsync(MaintenanceWindow window, CancellationToken ct)
    {
        try
        {
            await HandleMaintenanceStartedInternalAsync(window, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "UptimeTrackerService: HandleMaintenanceStartedAsync failed for {Window}.", window.DisplayName);
            try
            {
                await mediator.Publish(AppLogEntryOccurred.Error(LogSource,
                    $"Uptime tracker: failed to process Maintenance start for {window.DisplayName} — {ex.GetType().Name}: {ex.Message}."), CancellationToken.None);
            }
            catch { /* best effort */ }
        }
    }

    private async Task HandleMaintenanceStartedInternalAsync(MaintenanceWindow window, CancellationToken ct)
    {
```

(The original `HandleMaintenanceStartedAsync`'s full body becomes `HandleMaintenanceStartedInternalAsync`'s body, unchanged.)

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test AdminConsole.Tests/AdminConsole.Tests.csproj --filter FullyQualifiedName~UptimeTrackerServiceTests -v n`
Expected: PASS

- [ ] **Step 5: Run the full suite and build**

Run: `dotnet build AdminConsole.sln` — expect 0 warnings/0 errors.
Run: `dotnet test AdminConsole.Tests/AdminConsole.Tests.csproj` — expect all tests (the new one plus every pre-existing one) green.

- [ ] **Step 6: Commit**

```bash
git add AdminConsole.Infrastructure/Monitoring/UptimeTrackerService.cs AdminConsole.Tests/Monitoring/UptimeTrackerServiceTests.cs
git commit -m "fix: prevent UptimeTrackerService handler failures from killing PingMonitorService (audit 7.1)"
```

---

### Task 2: Finding 2.1 — `SlaReportJob.RunWeeklyAsync` needs the same resilience as `BackupMonitorJob.RunAsync`

**Files:**
- Modify: `AdminConsole.Infrastructure/Reports/SlaReportJob.cs`
- Test: Create `AdminConsole.Tests/Reports/SlaReportJobTests.cs`

**Interfaces:**
- Consumes: nothing from other tasks.
- Produces: nothing other tasks rely on.

- [ ] **Step 1: Write the failing test**

Create `AdminConsole.Tests/Reports/SlaReportJobTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test AdminConsole.Tests/AdminConsole.Tests.csproj --filter FullyQualifiedName~SlaReportJobTests -v n`
Expected: FAIL — `RunWeeklyAsync` currently lets the `InvalidOperationException` propagate directly (no error log gets published, and there is currently no second `mediator.Publish` call at all, so `Assert.Contains` also has nothing to find even incidentally).

- [ ] **Step 3: Implement the fix**

In `AdminConsole.Infrastructure/Reports/SlaReportJob.cs`, wrap the method body:

```csharp
    [DisableConcurrentExecution(timeoutInSeconds: 10)]
    public async Task RunWeeklyAsync(CancellationToken ct = default)
    {
        var to   = DateTimeOffset.Now;
        var from = to.AddDays(-7);

        var report = slaReportService.Generate(new SlaReportRequest { From = from, To = to });

        logger.LogInformation(
            "SlaReportJob: weekly report {From}–{To}, {Servers} servers, overall={Overall}%.",
            from, to, report.Servers.Count, report.OverallUptimePercent);

        var overallText = report.OverallUptimePercent is { } p
            ? $"{p:0.00}%"
            : "n/a (no servers in the report)";

        await mediator.Publish(AppLogEntryOccurred.Info(LogSource,
            $"Weekly SLA report ({from:dd.MM}–{to:dd.MM}): overall uptime {overallText}, " +
            $"{report.Servers.Sum(s => s.IncidentCount)} incident(s) across {report.Servers.Count} server(s)."), ct);
    }
```

becomes:

```csharp
    // Bug fix (2026-08-23, audit Finding 2.1): mirrors the exact pattern
    // already applied to BackupMonitorJob.RunAsync — log a visible
    // AppLogEntryOccurred.Error before rethrowing, so a failure here shows
    // up in the in-app Logs UI, not just as an invisible Hangfire "Failed"
    // entry. Rethrowing (not swallowing) is correct here specifically
    // because this is a Hangfire job, not an infinite MediatR loop — its
    // own retry mechanism is the right recovery path.
    [DisableConcurrentExecution(timeoutInSeconds: 10)]
    public async Task RunWeeklyAsync(CancellationToken ct = default)
    {
        try
        {
            var to   = DateTimeOffset.Now;
            var from = to.AddDays(-7);

            var report = slaReportService.Generate(new SlaReportRequest { From = from, To = to });

            logger.LogInformation(
                "SlaReportJob: weekly report {From}–{To}, {Servers} servers, overall={Overall}%.",
                from, to, report.Servers.Count, report.OverallUptimePercent);

            var overallText = report.OverallUptimePercent is { } p
                ? $"{p:0.00}%"
                : "n/a (no servers in the report)";

            await mediator.Publish(AppLogEntryOccurred.Info(LogSource,
                $"Weekly SLA report ({from:dd.MM}–{to:dd.MM}): overall uptime {overallText}, " +
                $"{report.Servers.Sum(s => s.IncidentCount)} incident(s) across {report.Servers.Count} server(s)."), ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "SlaReportJob: cycle failed.");
            await mediator.Publish(AppLogEntryOccurred.Error(LogSource,
                $"Weekly SLA report failed — {ex.GetType().Name}: {ex.Message}."), CancellationToken.None);
            throw;
        }
    }
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test AdminConsole.Tests/AdminConsole.Tests.csproj --filter FullyQualifiedName~SlaReportJobTests -v n`
Expected: PASS

- [ ] **Step 5: Run the full suite and build**

Run: `dotnet build AdminConsole.sln` — expect 0 warnings/0 errors.
Run: `dotnet test AdminConsole.Tests/AdminConsole.Tests.csproj` — expect all tests green.

- [ ] **Step 6: Commit**

```bash
git add AdminConsole.Infrastructure/Reports/SlaReportJob.cs AdminConsole.Tests/Reports/SlaReportJobTests.cs
git commit -m "fix: add resilience + visible error logging to SlaReportJob (audit 2.1)"
```

---

### Task 3: Findings 6.1 + 5.1 — cap `LogsController`'s `take`, add a 90-day `AppLogEntries` retention job

**Files:**
- Modify: `AdminConsole.Api/Controllers/LogsController.cs`
- Modify: `AdminConsole.Domain/Abstractions/IAppLogRepository.cs`
- Modify: `AdminConsole.Infrastructure/Data/Repositories/AppLogRepository.cs`
- Create: `AdminConsole.Infrastructure/Monitoring/AppLogRetentionJob.cs`
- Modify: `AdminConsole.Api/Program.cs`
- Test: Create `AdminConsole.Tests/Monitoring/AppLogRetentionJobTests.cs`

**Interfaces:**
- Consumes: nothing from other tasks.
- Produces: `IAppLogRepository.DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default) : Task<int>` — a new repository method, used only within this task.

- [ ] **Step 1: Write the failing test**

Create `AdminConsole.Tests/Monitoring/AppLogRetentionJobTests.cs`:

```csharp
using AdminConsole.Domain.Models;
using AdminConsole.Infrastructure.Data;
using AdminConsole.Infrastructure.Data.Repositories;
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
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test AdminConsole.Tests/AdminConsole.Tests.csproj --filter FullyQualifiedName~AppLogRetentionJobTests -v n`
Expected: FAIL to compile — `AppLogRetentionJob` and `IAppLogRepository.DeleteOlderThanAsync` don't exist yet.

- [ ] **Step 3: Add `DeleteOlderThanAsync` to the repository interface and implementation**

In `AdminConsole.Domain/Abstractions/IAppLogRepository.cs`, add to the interface:

```csharp
    /// <summary>Deletes every entry with Timestamp strictly older than cutoff. Returns the count removed.</summary>
    Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default);
```

In `AdminConsole.Infrastructure/Data/Repositories/AppLogRepository.cs`, add:

```csharp
    // Bug fix (2026-08-23, audit Finding 6.1): AppLogEntries had no
    // retention policy and grew forever — every background service logs to
    // it every cycle. ExecuteDeleteAsync (EF Core bulk delete) avoids
    // loading however many million rows might match into memory first.
    // Routed through the same Polly retry pipeline as SaveChangesAsync —
    // bulk operations bypass the change tracker and SaveChangesAsync
    // entirely, so they need their own retry wrapping.
    public Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default) =>
        SqliteRetryPolicy.Pipeline.ExecuteAsync(
            async token => await Context.AppLogEntries.Where(e => e.Timestamp < cutoff).ExecuteDeleteAsync(token),
            ct).AsTask();
```

- [ ] **Step 4: Create `AppLogRetentionJob`**

Create `AdminConsole.Infrastructure/Monitoring/AppLogRetentionJob.cs`:

```csharp
using AdminConsole.Domain.Abstractions;
using AdminConsole.Domain.Events;
using Hangfire;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AdminConsole.Infrastructure.Monitoring;

/// <summary>
/// Bug fix (2026-08-23, audit Finding 6.1): AppLogEntries had no
/// retention/pruning policy anywhere — every background service (Ping,
/// RDP, Zabbix, Backup, Uptime) writes to it every cycle, forever. Daily
/// Hangfire recurring job, same shape as BackupMonitorJob/SlaReportJob.
/// 90 days is a reasonable default for infrastructure/operational logs —
/// long enough to investigate an incident from a few weeks back, short
/// enough to keep the table (and the SQLite file) bounded.
/// </summary>
public sealed class AppLogRetentionJob(
    IAppLogRepository            repository,
    ILogger<AppLogRetentionJob>  logger,
    IMediator                    mediator)
{
    private const string LogSource     = "AppLogRetention";
    private const int    RetentionDays = 90;

    [DisableConcurrentExecution(timeoutInSeconds: 10)]
    public async Task RunAsync(CancellationToken ct = default)
    {
        try
        {
            var cutoff = DateTimeOffset.Now.AddDays(-RetentionDays);
            int removed = await repository.DeleteOlderThanAsync(cutoff, ct);

            if (removed > 0)
            {
                await mediator.Publish(AppLogEntryOccurred.Info(LogSource,
                    $"Removed {removed} log entr{(removed == 1 ? "y" : "ies")} older than {RetentionDays} days."), ct);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "AppLogRetentionJob: cycle failed.");
            await mediator.Publish(AppLogEntryOccurred.Error(LogSource,
                $"Log retention cleanup failed — {ex.GetType().Name}: {ex.Message}."), CancellationToken.None);
            throw;
        }
    }
}
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test AdminConsole.Tests/AdminConsole.Tests.csproj --filter FullyQualifiedName~AppLogRetentionJobTests -v n`
Expected: PASS

- [ ] **Step 6: Clamp `take` in `LogsController`**

In `AdminConsole.Api/Controllers/LogsController.cs`, change:

```csharp
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<AppLogEntry>>> Get(
        [FromQuery] int take = 1000,
        [FromQuery] DateTimeOffset? before = null,
        [FromQuery] DateTimeOffset? after = null,
        [FromQuery] string? search = null,
        CancellationToken ct = default) =>
        Ok(await repository.GetRecentAsync(take, before, after, search, ct));
```

to:

```csharp
    // Bug fix (2026-08-23, audit Finding 5.1): take had no upper bound and
    // flowed straight into EF Core's Take() against a table with no
    // retention policy of its own (see AppLogRetentionJob) — a single
    // request with an absurd take forced a full-table sort+serialize.
    private const int MaxTake = 5000;

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<AppLogEntry>>> Get(
        [FromQuery] int take = 1000,
        [FromQuery] DateTimeOffset? before = null,
        [FromQuery] DateTimeOffset? after = null,
        [FromQuery] string? search = null,
        CancellationToken ct = default) =>
        Ok(await repository.GetRecentAsync(Math.Clamp(take, 1, MaxTake), before, after, search, ct));
```

- [ ] **Step 7: Register the recurring job in `Program.cs`**

In `AdminConsole.Api/Program.cs`, find the Hangfire recurring-job registration block:

```csharp
    recurringJobs.AddOrUpdate<BackupMonitorJob>(
        "backup-monitor",
        job => job.RunAsync(CancellationToken.None),
        EveryNMinutesCron(monitoringSettings.BackupPollIntervalMinutes));

    recurringJobs.AddOrUpdate<SlaReportJob>(
        "sla-report-weekly",
        job => job.RunWeeklyAsync(CancellationToken.None),
        Cron.Weekly());
```

Add a third registration right after it, in the same block:

```csharp
    recurringJobs.AddOrUpdate<AppLogRetentionJob>(
        "app-log-retention",
        job => job.RunAsync(CancellationToken.None),
        Cron.Daily());
```

Also register the job class for DI, alongside `BackupMonitorJob`'s own registration:

```csharp
builder.Services.AddScoped<BackupMonitorJob>();
```

— add directly below it:

```csharp
builder.Services.AddScoped<AppLogRetentionJob>();
```

- [ ] **Step 8: Run the full suite and build**

Run: `dotnet build AdminConsole.sln` — expect 0 warnings/0 errors.
Run: `dotnet test AdminConsole.Tests/AdminConsole.Tests.csproj` — expect all tests green.

- [ ] **Step 9: Commit**

```bash
git add AdminConsole.Api/Controllers/LogsController.cs AdminConsole.Api/Program.cs AdminConsole.Domain/Abstractions/IAppLogRepository.cs AdminConsole.Infrastructure/Data/Repositories/AppLogRepository.cs AdminConsole.Infrastructure/Monitoring/AppLogRetentionJob.cs AdminConsole.Tests/Monitoring/AppLogRetentionJobTests.cs
git commit -m "fix: clamp Logs 'take' and add 90-day AppLogEntries retention job (audit 6.1, 5.1)"
```

---

### Task 4: Finding 3.1 — remove the unused Event Log feature entirely

**Files:**
- Delete: `AdminConsole.Infrastructure/Remote/EventLogService.cs`
- Delete: `AdminConsole.Infrastructure/Remote/RemoteEventLogService.cs`
- Delete: `AdminConsole.Domain/Events/EventLogUpdatedOccurred.cs`
- Delete: `AdminConsole.Domain/Models/EventLogEntry.cs`
- Modify: `AdminConsole.Infrastructure/Remote/WinEventLogReader.cs` (keep only `IsReachableAsync`, still used by `BackupCheckEvaluator`)
- Modify: `AdminConsole.Api/Program.cs`
- Modify: `AdminConsole.Api/Realtime/SignalRBroadcastHandler.cs`

**Interfaces:**
- Consumes: nothing from other tasks.
- Produces: nothing other tasks rely on.

**Verified before writing this task:** `grep -rn "TrimMessage|EventLogEntry|EventSeverity|IsReachableAsync" --include=*.cs` finds exactly 6 files: the 4 being deleted/emptied below, plus `AdminConsole.Infrastructure/Monitoring/BackupCheckEvaluator.cs` (uses only `IsReachableAsync`) and `SignalRBroadcastHandler.cs` (the notification handler being removed here). No test file references any of these types.

- [ ] **Step 1: Delete the four dead files**

```bash
git rm AdminConsole.Infrastructure/Remote/EventLogService.cs
git rm AdminConsole.Infrastructure/Remote/RemoteEventLogService.cs
git rm AdminConsole.Domain/Events/EventLogUpdatedOccurred.cs
git rm AdminConsole.Domain/Models/EventLogEntry.cs
```

- [ ] **Step 2: Trim `WinEventLogReader.cs` down to just `IsReachableAsync`**

Replace the entire contents of `AdminConsole.Infrastructure/Remote/WinEventLogReader.cs` with:

```csharp
using System.Net.NetworkInformation;

namespace AdminConsole.Infrastructure.Remote;

/// <summary>
/// Bug fix (2026-08-23, audit Finding 3.1): this class used to also contain
/// ReadErrors/MapRecord/TrimMessage for reading the Windows Event Log — that
/// whole feature (this class's Event-Log-specific half, plus EventLogService
/// and RemoteEventLogService) was fully built, registered, and running in
/// the background, but had zero consumers anywhere in the product (no
/// controller ever called RemoteEventLogService.FetchAsync, and no frontend
/// code ever subscribed to EventLogUpdatedOccurred). Removed per YAGNI —
/// recoverable from git history if this is needed later. IsReachableAsync
/// survives because BackupCheckEvaluator uses it as a generic pre-check
/// before scanning a UNC path.
/// </summary>
public static class WinEventLogReader
{
    public static async Task<bool> IsReachableAsync(
        string host, int timeoutMs, CancellationToken ct)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping
                .SendPingAsync(host, timeoutMs)
                .WaitAsync(ct)
                .ConfigureAwait(false);
            return reply.Status == IPStatus.Success;
        }
        catch { return false; }
    }
}
```

- [ ] **Step 3: Remove the DI registrations in `Program.cs`**

Delete this block from `AdminConsole.Api/Program.cs` (it currently reads):

```csharp
// T4.7 — EventLogService (BackgroundService) + WinEventLogReader (static, no DI) + RemoteEventLogService (on-demand).
builder.Services.AddSingleton<EventLogService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<EventLogService>());
builder.Services.AddSingleton<RemoteEventLogService>();

```

Remove it entirely (including the blank line after it, so the surrounding sections join up cleanly).

- [ ] **Step 4: Remove the handler from `SignalRBroadcastHandler`**

In `AdminConsole.Api/Realtime/SignalRBroadcastHandler.cs`, remove `INotificationHandler<EventLogUpdatedOccurred>,` from the class's interface list, and remove this method:

```csharp
    public Task Handle(EventLogUpdatedOccurred n, CancellationToken ct) => Send(Logs, n, ct);

```

- [ ] **Step 5: Build and confirm nothing else references the removed types**

Run: `dotnet build AdminConsole.sln`
Expected: 0 warnings/0 errors. If the build fails on a missing reference, that reference was not covered by this task's verification grep — read the error, find the file, and add its fix to this task before proceeding (do not leave a broken build).

- [ ] **Step 6: Run the full suite**

Run: `dotnet test AdminConsole.Tests/AdminConsole.Tests.csproj` — expect all tests green (no test referenced the removed types, per this task's up-front verification).

- [ ] **Step 7: Commit**

```bash
git add -A -- AdminConsole.Infrastructure/Remote AdminConsole.Domain/Events AdminConsole.Domain/Models AdminConsole.Api/Program.cs AdminConsole.Api/Realtime/SignalRBroadcastHandler.cs
git commit -m "chore: remove unused Event Log feature (audit 3.1) — recoverable from git history if needed"
```

---

### Task 5: Finding 1.1 — granular per-step `MigrationMarker`s

**Files:**
- Modify: `AdminConsole.Infrastructure/Data/Entities/MigrationMarker.cs`
- Modify: `AdminConsole.Infrastructure/Data/Configurations/MigrationMarkerConfiguration.cs`
- Create: a new EF Core migration under `AdminConsole.Infrastructure/Migrations/` (exact filename assigned by the tool at generation time)
- Modify: `AdminConsole.Migration/MigrationRunner.cs`
- Test: Modify `AdminConsole.Tests/Migration/MigrationRunnerTests.cs`

**Interfaces:**
- Consumes: nothing from other tasks.
- Produces: nothing other tasks rely on.

- [ ] **Step 1: Modify the entity and its configuration**

Replace `AdminConsole.Infrastructure/Data/Entities/MigrationMarker.cs` with:

```csharp
namespace AdminConsole.Infrastructure.Data.Entities;

/// <summary>
/// Internal flag marking "this one-time migration STEP has already run".
/// AdminConsole.Migration checks this per step before writing, so a repeat
/// run doesn't duplicate data AND doesn't re-run a step that already
/// succeeded (Phase 2, T2.6; granular per-step markers added 2026-08-23,
/// audit Finding 1.1 — a single shared marker written only after all four
/// steps succeeded meant a crash between steps, followed by a well-
/// intentioned retry, could silently re-run MigrateUserSettingsAsync and
/// revert live AppSettings changes made in between). An infrastructure
/// entity — it has no business meaning beyond persistence, so it doesn't
/// live in AdminConsole.Domain.
/// </summary>
public sealed class MigrationMarker
{
    public int Id { get; set; }

    /// <summary>Null for a legacy pre-2026-08-23 row (the old single global marker) — never written by new code.</summary>
    public string? Step { get; set; }

    public DateTimeOffset? CompletedAtUtc { get; set; }
}
```

Replace `AdminConsole.Infrastructure/Data/Configurations/MigrationMarkerConfiguration.cs` with:

```csharp
using AdminConsole.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AdminConsole.Infrastructure.Data.Configurations;

public sealed class MigrationMarkerConfiguration : IEntityTypeConfiguration<MigrationMarker>
{
    public void Configure(EntityTypeBuilder<MigrationMarker> builder)
    {
        builder.ToTable("MigrationMarker");
        builder.HasKey(m => m.Id);

        // SQLite allows multiple NULLs in a unique index (they're never
        // considered equal to each other) — the pre-existing legacy row
        // (Step == null) coexists fine alongside the four new named rows.
        builder.HasIndex(m => m.Step).IsUnique();
    }
}
```

- [ ] **Step 2: Generate the EF Core migration**

Run (from the repository root):

```bash
dotnet ef migrations add AddGranularMigrationMarkers --project AdminConsole.Infrastructure
```

This creates a new pair of files under `AdminConsole.Infrastructure/Migrations/` named `<timestamp>_AddGranularMigrationMarkers.cs` and `<timestamp>_AddGranularMigrationMarkers.Designer.cs` — open the non-Designer one (the exact filename depends on when you run this command).

- [ ] **Step 3: Add a data-preservation statement to the generated migration's `Up()`**

The generated `Up()` method will contain an `AddColumn` call for `Step` and a `CreateIndex` call for the new unique index (EF Core generates these automatically from the configuration change in Step 1 — do not hand-write them, just confirm they're there). Immediately after those two auto-generated statements, inside the same `Up()` method, add:

```csharp
            // Bug fix (2026-08-23, audit Finding 1.1): if a legacy marker row
            // (Step IS NULL) shows the migration was already fully completed
            // before this schema change, seed all four new per-step markers
            // as completed too — otherwise an already-migrated production
            // database would look like it needs to re-run every step from
            // scratch the next time the tool is invoked.
            migrationBuilder.Sql("""
                INSERT INTO MigrationMarker (Step, CompletedAtUtc)
                SELECT v.step, m.CompletedAtUtc
                FROM (VALUES ('Downtime'), ('Maintenance'), ('Backups'), ('UserSettings')) AS v(step), MigrationMarker m
                WHERE m.Step IS NULL AND m.CompletedAtUtc IS NOT NULL;
                """);
```

Leave `Down()` exactly as generated — rolling back this migration drops the `Step` column along with whatever rows were seeded, which is an acceptable, standard EF Core rollback for an additive schema change.

- [ ] **Step 4: Refactor `MigrationRunner.RunAsync` to check/set four independent markers**

In `AdminConsole.Migration/MigrationRunner.cs`, replace the whole `RunAsync` method:

```csharp
    public async Task<MigrationSummary> RunAsync(MigrationOptions options, CancellationToken ct = default)
    {
        var marker = await db.MigrationMarkers.FirstOrDefaultAsync(ct);
        if (marker?.CompletedAtUtc is not null)
        {
            logger.LogInformation(
                "Migration was already completed at {CompletedAt} — re-running does nothing.",
                marker.CompletedAtUtc);
            return new MigrationSummary(true, 0, 0, 0, false, 0);
        }

        int downtimeCount    = await MigrateDowntimeAsync(options.OldLogsDirectory, ct);
        int maintenanceCount = await MigrateMaintenanceAsync(options.OldLogsDirectory, ct);
        int backupCount      = await MigrateBackupsAsync(options.OldLogsDirectory, ct);
        var (settingsMigrated, telegramCount) = await MigrateUserSettingsAsync(options.OldUserSettingsPath, ct);

        marker ??= new MigrationMarker();
        marker.CompletedAtUtc = DateTimeOffset.UtcNow;
        if (marker.Id == 0) db.MigrationMarkers.Add(marker);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Migration completed: {Downtime} downtime, {Maintenance} maintenance, {Backups} backup state(s), " +
            "settings={Settings}, {Telegram} telegram user(s).",
            downtimeCount, maintenanceCount, backupCount, settingsMigrated, telegramCount);

        return new MigrationSummary(false, downtimeCount, maintenanceCount, backupCount, settingsMigrated, telegramCount);
    }
```

with:

```csharp
    // Bug fix (2026-08-23, audit Finding 1.1): each of the four steps now
    // has its own marker, checked and written independently, right after
    // that specific step succeeds — not one shared marker written only
    // after all four succeed. A crash between steps 2 and 3, followed by a
    // retry, now correctly re-runs only steps 3 and 4 instead of silently
    // re-running everything (including MigrateUserSettingsAsync, which
    // used to be able to revert live AppSettings changes made in between).
    public async Task<MigrationSummary> RunAsync(MigrationOptions options, CancellationToken ct = default)
    {
        bool downtimeDone    = await IsStepCompletedAsync("Downtime", ct);
        bool maintenanceDone = await IsStepCompletedAsync("Maintenance", ct);
        bool backupsDone     = await IsStepCompletedAsync("Backups", ct);
        bool settingsDone    = await IsStepCompletedAsync("UserSettings", ct);

        if (downtimeDone && maintenanceDone && backupsDone && settingsDone)
        {
            logger.LogInformation("Migration was already completed for every step — re-running does nothing.");
            return new MigrationSummary(true, 0, 0, 0, false, 0);
        }

        int downtimeCount = downtimeDone ? 0 : await MigrateDowntimeAsync(options.OldLogsDirectory, ct);
        if (!downtimeDone) await MarkStepCompletedAsync("Downtime", ct);

        int maintenanceCount = maintenanceDone ? 0 : await MigrateMaintenanceAsync(options.OldLogsDirectory, ct);
        if (!maintenanceDone) await MarkStepCompletedAsync("Maintenance", ct);

        int backupCount = backupsDone ? 0 : await MigrateBackupsAsync(options.OldLogsDirectory, ct);
        if (!backupsDone) await MarkStepCompletedAsync("Backups", ct);

        var (settingsMigrated, telegramCount) = settingsDone
            ? (false, 0)
            : await MigrateUserSettingsAsync(options.OldUserSettingsPath, ct);
        if (!settingsDone) await MarkStepCompletedAsync("UserSettings", ct);

        logger.LogInformation(
            "Migration step summary: {Downtime} downtime, {Maintenance} maintenance, {Backups} backup state(s), " +
            "settings={Settings}, {Telegram} telegram user(s). (0/false for any step already completed on a prior run.)",
            downtimeCount, maintenanceCount, backupCount, settingsMigrated, telegramCount);

        return new MigrationSummary(false, downtimeCount, maintenanceCount, backupCount, settingsMigrated, telegramCount);
    }

    private async Task<bool> IsStepCompletedAsync(string step, CancellationToken ct) =>
        await db.MigrationMarkers.AnyAsync(m => m.Step == step && m.CompletedAtUtc != null, ct);

    private async Task MarkStepCompletedAsync(string step, CancellationToken ct)
    {
        db.MigrationMarkers.Add(new MigrationMarker { Step = step, CompletedAtUtc = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(ct);
    }
```

Add `using AdminConsole.Infrastructure.Data.Entities;` at the top of the file if it isn't already there (`MigrationMarker` lives in that namespace).

- [ ] **Step 5: Run the existing tests to confirm they still pass unchanged**

Run: `dotnet test AdminConsole.Tests/AdminConsole.Tests.csproj --filter FullyQualifiedName~MigrationRunnerTests -v n`
Expected: PASS — `FirstRun_MigratesFixtureData_WithDeduplicationAndExpiredFilter` (no markers exist yet, so all four steps run, identical counts to before) and `SecondRun_IsIdempotent_NoDuplicateRows` (after a full first run all four markers exist, so `AlreadyCompleted` is `true` on the second run, exactly as before) both already exercise this correctly with zero test changes needed.

- [ ] **Step 6: Write the new regression test proving the fix**

Add this test to `AdminConsole.Tests/Migration/MigrationRunnerTests.cs` (inside the existing `MigrationRunnerTests` class, alongside the other two `[Fact]` methods):

```csharp
    [Fact]
    public async Task PartialCompletion_AppSettingsAlreadyMigrated_SurvivesAdminChangeOnRetry()
    {
        // Simulate: everything except Backups completed on an earlier,
        // interrupted run (Downtime/Maintenance/UserSettings all marked done).
        await using (var scope = _provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AdminConsoleDbContext>();
            db.MigrationMarkers.AddRange(
                new AdminConsole.Infrastructure.Data.Entities.MigrationMarker { Step = "Downtime", CompletedAtUtc = DateTimeOffset.UtcNow },
                new AdminConsole.Infrastructure.Data.Entities.MigrationMarker { Step = "Maintenance", CompletedAtUtc = DateTimeOffset.UtcNow },
                new AdminConsole.Infrastructure.Data.Entities.MigrationMarker { Step = "UserSettings", CompletedAtUtc = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        // The admin, now using the live app after the "crash", flips a
        // toggle manually — deliberately the OPPOSITE of the legacy
        // fixture's "RdpMonitoringEnabled": false.
        await using (var scope = _provider.CreateAsyncScope())
        {
            var appSettings = scope.ServiceProvider.GetRequiredService<AdminConsole.Domain.Abstractions.IAppSettingsRepository>();
            var current = await appSettings.GetAsync();
            current.RdpMonitoringEnabled = true;
            await appSettings.SaveAsync(current);
        }

        await using (var scope = _provider.CreateAsyncScope())
        {
            var runner = scope.ServiceProvider.GetRequiredService<MigrationRunner>();
            var summary = await runner.RunAsync(Options);

            Assert.False(summary.AlreadyCompleted);   // Backups still ran — not everything was done
            Assert.False(summary.AppSettingsMigrated); // UserSettings step was correctly SKIPPED this time
            Assert.Equal(0, summary.DowntimeRecords);
            Assert.Equal(0, summary.MaintenanceWindows);
            Assert.Equal(2, summary.BackupCheckStates); // the one step that hadn't run yet
        }

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AdminConsoleDbContext>();
        var settings = await verifyDb.AppSettings.SingleAsync();
        Assert.True(settings.RdpMonitoringEnabled); // the admin's manual change survived — NOT reverted to the legacy "false"
    }
```

- [ ] **Step 7: Run the test to verify it passes**

Run: `dotnet test AdminConsole.Tests/AdminConsole.Tests.csproj --filter FullyQualifiedName~MigrationRunnerTests -v n`
Expected: PASS (all three `[Fact]`s in this class).

- [ ] **Step 8: Run the full suite and build**

Run: `dotnet build AdminConsole.sln` — expect 0 warnings/0 errors.
Run: `dotnet test AdminConsole.Tests/AdminConsole.Tests.csproj` — expect all tests green.

- [ ] **Step 9: Commit**

```bash
git add AdminConsole.Infrastructure/Data/Entities/MigrationMarker.cs AdminConsole.Infrastructure/Data/Configurations/MigrationMarkerConfiguration.cs AdminConsole.Infrastructure/Migrations/ AdminConsole.Migration/MigrationRunner.cs AdminConsole.Tests/Migration/MigrationRunnerTests.cs
git commit -m "fix: track migration completion per step, not one global marker (audit 1.1)"
```

---

### Task 6: Finding 8.1 — let `AuthContext` recover from a transient denial

**Files:**
- Modify: `adminconsole-web/src/lib/auth/AuthContext.tsx`

**Interfaces:**
- Consumes: nothing from other tasks.
- Produces: nothing other tasks rely on.

**No automated test for this task**: `adminconsole-web/package.json` defines no `test` script and there is no Vitest/Jest config anywhere in `adminconsole-web/` — this repo currently has zero frontend test infrastructure. Verification is manual, via the dev server (Step 3 below).

- [ ] **Step 1: Remove the one-way freeze on `reportAuthorized`**

In `adminconsole-web/src/lib/auth/AuthContext.tsx`, replace:

```tsx
  // reportDenied always fires (even after an initial 'authorized' —
  // e.g. the session became invalid later) and "freezes" resolvedRef
  // so a late reportAuthorized from another channel can't roll denied
  // back.
  const reportDenied = useCallback(() => {
    resolvedRef.current = true
    setStatus('denied')
  }, [])

  // reportAuthorized only resolves the initial race (checking →
  // authorized) — if the state is already resolved (by anyone), a
  // repeat call is a no-op.
  const reportAuthorized = useCallback(() => {
    if (resolvedRef.current) return
    resolvedRef.current = true
    setStatus('authorized')
  }, [])
```

with:

```tsx
  // Bug fix (2026-08-23, audit Finding 8.1): reportDenied used to
  // permanently freeze the app in 'denied' for the rest of the session —
  // this app polls many independent REST/SignalR channels, and every one
  // of them calls reportDenied on its own 401/403. A single transient
  // hiccup on any one of them (a dropped connection during Kerberos
  // ticket renewal, a brief reverse-proxy blip) used to lock the whole UI
  // behind a full-screen "Access Denied" for the rest of the session, with
  // no way back short of a manual reload. Denial and authorization are now
  // symmetric, ordinary status transitions: whichever channel reports
  // last wins. resolvedRef still serves its original purpose — ending the
  // initial 'checking' loading state on the FIRST signal from any
  // channel — it just no longer also acts as a permanent one-way lock.
  const reportDenied = useCallback(() => {
    resolvedRef.current = true
    setStatus('denied')
  }, [])

  const reportAuthorized = useCallback(() => {
    resolvedRef.current = true
    setStatus('authorized')
  }, [])
```

- [ ] **Step 2: Verify types and lint**

Run (from `adminconsole-web/`): `npx tsc --noEmit`
Expected: no errors.

Run (from `adminconsole-web/`): `npx oxlint src/lib/auth/AuthContext.tsx`
Expected: no new warnings.

- [ ] **Step 3: Verify in the browser that the app still loads and authorizes normally**

Start the dev server (`name: "adminconsole-web"` in `.claude/launch.json`, or `npm run dev` from `adminconsole-web/`) with the backend also running. Confirm the app reaches `authorized` status (no `AccessDenied` screen, dashboard renders) — this proves the refactor didn't break the ordinary, no-error startup path.

Reproducing the actual fix live would require a real mid-session 401/403 on one specific channel followed by a success on another — that means revoking the AD group membership of the account running the browser session for a moment and restoring it, which isn't practical to trigger on demand in this environment. Treat the self-healing behavior itself as **verified by code reading, not live-simulated**: re-read the new `reportDenied`/`reportAuthorized` pair and confirm both are now simple, symmetric `setStatus` calls with no `if (resolvedRef.current) return` guard on either one — that symmetry is the entire fix, and it's fully visible in the diff. State this plainly when reporting Task 6 complete, rather than claiming a live denial-then-recovery was observed.

- [ ] **Step 4: Commit**

```bash
git add adminconsole-web/src/lib/auth/AuthContext.tsx
git commit -m "fix: let AuthContext recover from a transient denial instead of locking permanently (audit 8.1)"
```

---
