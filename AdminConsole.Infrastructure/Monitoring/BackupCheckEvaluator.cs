using AdminConsole.Domain.Models;
using AdminConsole.Infrastructure.Remote;

namespace AdminConsole.Infrastructure.Monitoring;

/// <summary>
/// Pure logic for checking a single backup (FileAge). Stateless, persists
/// nothing, and sends no notifications — the file system is the only
/// input, BackupCheckResult the only output. Deliberately kept separate
/// from BackupMonitorJob (Phase 4, Hangfire): it can be called directly
/// and tested manually against a test folder, without DI/a host.
///
/// Anti-flapping and the LastConfirmed* logic are NOT part of this — that's
/// already the job's responsibility: it calls Evaluate and decides whether
/// the "raw" result is stable enough to become confirmed.
///
/// Carried over almost unchanged (T4.4) — its one dependency,
/// WinEventLogReader.IsReachableAsync, moves along with it.
/// </summary>
public sealed class BackupCheckEvaluator
{
    /// <summary>
    /// Runs a single check. <paramref name="history"/> is the current
    /// (as of before this call) rolling size history for this server+Kind;
    /// used only for comparison, nothing in it is modified.
    /// </summary>
    public async Task<BackupCheckResult> EvaluateAsync(
        BackupCheckDefinition definition,
        BackupKind kind,
        IReadOnlyList<BackupSample> history,
        CancellationToken ct = default)
    {
        var pattern = kind == BackupKind.Full ? definition.FullPattern : definition.DiffPattern;
        var maxAgeHours = kind == BackupKind.Full ? definition.MaxAgeHoursFull : definition.MaxAgeHoursDiff;

        if (string.IsNullOrWhiteSpace(pattern))
        {
            // The caller (BackupMonitorJob) is expected to skip a Kind with
            // an empty pattern BEFORE calling Evaluate — this isn't
            // "Missing", it's "this check isn't configured for this server
            // at all".
            throw new ArgumentException(
                $"Pattern for {kind} is empty — don't call Evaluate for an unconfigured Kind.",
                nameof(kind));
        }

        // ── Stage A0 — quick UNC host reachability check BEFORE heavy I/O ──
        // Directory.Exists/EnumerateFiles against an unreachable network
        // share can block on a native Windows timeout for tens of seconds,
        // and CancellationToken doesn't help here (Task.Run doesn't cancel
        // an already-running synchronous call). A short ping (the same
        // approach as WinEventLogReader.IsReachableAsync) heads this off
        // in advance.
        if (TryGetUncHost(definition.Path, out var host))
        {
            var reachable = await WinEventLogReader
                .IsReachableAsync(host, timeoutMs: 1000, ct)
                .ConfigureAwait(false);

            if (!reachable)
                return BackupCheckResult.Unknown($"Host '{host}' is unreachable (ping timeout).");
        }

        // ── Stage A
        // Any access failure here is always Unknown, no exceptions
        // (no reason for unreachability is ever treated as Missing).
        FileInfo? newest;
        try
        {
            newest = await Task.Run(
                () => FindNewestMatchingFile(definition.Path, pattern), ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return BackupCheckResult.Unknown(ex.Message);
        }

        // ── Stage B
        if (newest is null)
            return BackupCheckResult.Missing();

        var sample = new BackupSample
        {
            ObservedAt = DateTimeOffset.Now,
            SizeBytes  = newest.Length
        };

        var age = DateTimeOffset.Now - newest.LastWriteTime;
        if (age.TotalHours > maxAgeHours)
            return BackupCheckResult.Stale(sample);

        if (history.Count < definition.MinSamplesForBaseline)
            return BackupCheckResult.Ok(sample); // not enough history yet to honestly evaluate the size

        var average = history.Average(s => (double)s.SizeBytes);
        if (average <= 0)
            return BackupCheckResult.Ok(sample); // guard against division by zero (History made up of zero-size entries only)

        var deviationPct = Math.Abs(sample.SizeBytes - average) / average * 100.0;

        return deviationPct > definition.SizeWarningThresholdPct
            ? BackupCheckResult.SizeWarning(sample)
            : BackupCheckResult.Ok(sample);
    }

    private static FileInfo? FindNewestMatchingFile(string path, string pattern)
    {
        if (!Directory.Exists(path))
            throw new IOException($"Path is unreachable: {path}");

        return new DirectoryInfo(path)
            .EnumerateFiles(pattern, SearchOption.TopDirectoryOnly)
            .MaxBy(f => f.LastWriteTime);
    }

    /// <summary>Extracts the host name from a UNC path (\\host\share\...). Returns false for local paths (C:\...).</summary>
    private static bool TryGetUncHost(string path, out string host)
    {
        host = string.Empty;
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith(@"\\", StringComparison.Ordinal))
            return false;

        var trimmed = path.TrimStart('\\');
        var sepIndex = trimmed.IndexOfAny(['\\', '/']);
        host = sepIndex > 0 ? trimmed[..sepIndex] : trimmed;
        return !string.IsNullOrWhiteSpace(host);
    }
}
