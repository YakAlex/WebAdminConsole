namespace AdminConsole.Domain.Models;

/// <summary>
/// Result of a SINGLE check attempt (one cycle, one server, one Kind).
/// "Raw" — no anti-flapping and no writing to LastConfirmed*; that's
/// BackupMonitorService's responsibility (Phase 2).
/// </summary>
public sealed class BackupCheckResult
{
    public required BackupOutcome Outcome { get; init; }

    /// <summary>Populated if a file matching the pattern was found (Ok / SizeWarning / Stale). Null for Missing and Unknown.</summary>
    public BackupSample? Sample { get; init; }

    /// <summary>Populated only for Outcome == Unknown — the Stage A exception text.</summary>
    public string? ErrorMessage { get; init; }

    public static BackupCheckResult Unknown(string error) =>
        new() { Outcome = BackupOutcome.Unknown, ErrorMessage = error };

    public static BackupCheckResult Missing() =>
        new() { Outcome = BackupOutcome.Missing };

    public static BackupCheckResult Stale(BackupSample sample) =>
        new() { Outcome = BackupOutcome.Stale, Sample = sample };

    public static BackupCheckResult Ok(BackupSample sample) =>
        new() { Outcome = BackupOutcome.Ok, Sample = sample };

    public static BackupCheckResult SizeWarning(BackupSample sample) =>
        new() { Outcome = BackupOutcome.SizeWarning, Sample = sample };
}
