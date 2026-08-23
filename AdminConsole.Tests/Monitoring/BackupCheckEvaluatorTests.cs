using AdminConsole.Domain.Models;
using AdminConsole.Infrastructure.Monitoring;

namespace AdminConsole.Tests.Monitoring;

/// <summary>
/// Unit tests for BackupCheckEvaluator's Stage A/B logic (file discovery,
/// age check, size-deviation check). BackupMonitorJobTests.cs's own doc
/// comment claims this class is "already covered by its own tests" — that
/// file never actually existed; this closes that gap. A fresh temp
/// directory per test instance (xUnit creates a new class instance per
/// [Fact]), no DB/DI needed since the evaluator is stateless.
/// </summary>
public sealed class BackupCheckEvaluatorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "AdminConsoleBackupEvaluatorTests_" + Guid.NewGuid());
    private readonly BackupCheckEvaluator _evaluator = new();

    public BackupCheckEvaluatorTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private BackupCheckDefinition MakeDefinition(
        int maxAgeHours = 26, int minSamplesForBaseline = 3, int sizeWarningThresholdPct = 30) => new()
    {
        Name = "Server1",
        Host = "Server1",
        Path = _dir,
        FullPattern = "backup_*.bak",
        DiffPattern = "",
        MaxAgeHoursFull = maxAgeHours,
        MaxAgeHoursDiff = maxAgeHours,
        SizeWarningThresholdPct = sizeWarningThresholdPct,
        MinSamplesForBaseline = minSamplesForBaseline
    };

    private void WriteBackupFile(long sizeBytes, DateTime lastWriteTime)
    {
        var path = Path.Combine(_dir, $"backup_{Guid.NewGuid():N}.bak");
        File.WriteAllBytes(path, new byte[sizeBytes]);
        File.SetLastWriteTime(path, lastWriteTime);
    }

    [Fact]
    public async Task EvaluateAsync_NoMatchingFile_ReturnsMissing()
    {
        var result = await _evaluator.EvaluateAsync(MakeDefinition(), BackupKind.Full, history: []);

        Assert.Equal(BackupOutcome.Missing, result.Outcome);
        Assert.Null(result.Sample);
    }

    [Fact]
    public async Task EvaluateAsync_PathDoesNotExist_ReturnsUnknown()
    {
        var definition = MakeDefinition();
        Directory.Delete(_dir);

        var result = await _evaluator.EvaluateAsync(definition, BackupKind.Full, history: []);

        Assert.Equal(BackupOutcome.Unknown, result.Outcome);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task EvaluateAsync_FreshFile_NotEnoughHistory_ReturnsOk()
    {
        WriteBackupFile(1000, DateTime.Now);

        var result = await _evaluator.EvaluateAsync(MakeDefinition(minSamplesForBaseline: 3), BackupKind.Full, history: []);

        Assert.Equal(BackupOutcome.Ok, result.Outcome);
        Assert.NotNull(result.Sample);
    }

    [Fact]
    public async Task EvaluateAsync_FileOlderThanMaxAge_ReturnsStale()
    {
        WriteBackupFile(1000, DateTime.Now.AddHours(-48));

        var result = await _evaluator.EvaluateAsync(MakeDefinition(maxAgeHours: 26), BackupKind.Full, history: []);

        Assert.Equal(BackupOutcome.Stale, result.Outcome);
        Assert.NotNull(result.Sample);
    }

    [Fact]
    public async Task EvaluateAsync_PicksNewestFile_WhenMultipleMatch()
    {
        WriteBackupFile(100, DateTime.Now.AddHours(-10));
        WriteBackupFile(999, DateTime.Now); // the newest — this size must be the one evaluated

        var result = await _evaluator.EvaluateAsync(MakeDefinition(), BackupKind.Full, history: []);

        Assert.Equal(BackupOutcome.Ok, result.Outcome);
        Assert.Equal(999, result.Sample!.SizeBytes);
    }

    [Fact]
    public async Task EvaluateAsync_SizeWithinThreshold_ReturnsOk()
    {
        WriteBackupFile(1000, DateTime.Now);
        var history = new List<BackupSample>
        {
            new() { ObservedAt = DateTimeOffset.Now.AddDays(-1), SizeBytes = 1000 },
            new() { ObservedAt = DateTimeOffset.Now.AddDays(-2), SizeBytes = 1000 },
            new() { ObservedAt = DateTimeOffset.Now.AddDays(-3), SizeBytes = 1000 },
        };

        var result = await _evaluator.EvaluateAsync(
            MakeDefinition(minSamplesForBaseline: 3, sizeWarningThresholdPct: 30), BackupKind.Full, history);

        Assert.Equal(BackupOutcome.Ok, result.Outcome);
    }

    [Fact]
    public async Task EvaluateAsync_SizeDeviatesBeyondThreshold_ReturnsSizeWarning()
    {
        // History average is 1000; new file is 2000 — 100% deviation, well past a 30% threshold.
        WriteBackupFile(2000, DateTime.Now);
        var history = new List<BackupSample>
        {
            new() { ObservedAt = DateTimeOffset.Now.AddDays(-1), SizeBytes = 1000 },
            new() { ObservedAt = DateTimeOffset.Now.AddDays(-2), SizeBytes = 1000 },
            new() { ObservedAt = DateTimeOffset.Now.AddDays(-3), SizeBytes = 1000 },
        };

        var result = await _evaluator.EvaluateAsync(
            MakeDefinition(minSamplesForBaseline: 3, sizeWarningThresholdPct: 30), BackupKind.Full, history);

        Assert.Equal(BackupOutcome.SizeWarning, result.Outcome);
    }

    [Fact]
    public async Task EvaluateAsync_HistoryAverageIsZero_GuardsAgainstDivisionByZero_ReturnsOk()
    {
        WriteBackupFile(1000, DateTime.Now);
        var history = new List<BackupSample>
        {
            new() { ObservedAt = DateTimeOffset.Now.AddDays(-1), SizeBytes = 0 },
            new() { ObservedAt = DateTimeOffset.Now.AddDays(-2), SizeBytes = 0 },
            new() { ObservedAt = DateTimeOffset.Now.AddDays(-3), SizeBytes = 0 },
        };

        var result = await _evaluator.EvaluateAsync(
            MakeDefinition(minSamplesForBaseline: 3), BackupKind.Full, history);

        Assert.Equal(BackupOutcome.Ok, result.Outcome);
    }

    [Fact]
    public async Task EvaluateAsync_EmptyPatternForRequestedKind_ThrowsArgumentException()
    {
        var definition = MakeDefinition();
        // DiffPattern is "" in MakeDefinition() — evaluating Diff for a
        // server where it isn't configured must throw, per the evaluator's
        // own contract (the caller is expected to have skipped this Kind).
        await Assert.ThrowsAsync<ArgumentException>(
            () => _evaluator.EvaluateAsync(definition, BackupKind.Diff, history: []));
    }

    [Fact]
    public async Task EvaluateAsync_UnreachableUncHost_ReturnsUnknown_BeforeTouchingTheFileSystem()
    {
        var definition = MakeDefinition() switch
        {
            var d => new BackupCheckDefinition
            {
                Name = d.Name, Host = d.Host,
                Path = @"\\adminconsole-test-host-that-does-not-exist\share",
                FullPattern = d.FullPattern, DiffPattern = d.DiffPattern,
                MaxAgeHoursFull = d.MaxAgeHoursFull, MaxAgeHoursDiff = d.MaxAgeHoursDiff,
                SizeWarningThresholdPct = d.SizeWarningThresholdPct, MinSamplesForBaseline = d.MinSamplesForBaseline
            }
        };

        var result = await _evaluator.EvaluateAsync(definition, BackupKind.Full, history: []);

        Assert.Equal(BackupOutcome.Unknown, result.Outcome);
        Assert.Contains("unreachable", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }
}
