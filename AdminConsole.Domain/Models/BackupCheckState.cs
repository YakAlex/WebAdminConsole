namespace AdminConsole.Domain.Models;

/// <summary>
/// Runtime state of a single check (server + Full/Diff). Persisted to
/// logs/backups.json (atomic write, the same pattern as
/// DowntimeRecord/MaintenanceWindow — temp file + File.Move(overwrite: true)).
/// </summary>
public sealed class BackupCheckState
{
    public string Name { get; init; } = string.Empty;

    public string Host { get; set; } = string.Empty;
    public BackupKind Kind       { get; init; }

    /// <summary>
    /// The current, CONFIRMED (post-anti-flapping) state — this is what's
    /// shown in the UI and what triggers a Telegram alert on a transition
    /// to Stale/Missing.
    /// </summary>
    public BackupOutcome Outcome { get; set; } = BackupOutcome.Unknown;

    /// <summary>When the last confirmed non-Unknown result occurred.</summary>
    public DateTimeOffset? LastConfirmedAt { get; set; }

    /// <summary>What that last confirmed non-Unknown result actually was.</summary>
    public BackupOutcome? LastConfirmedOutcome { get; set; }

    /// <summary>How many consecutive cycles the check has failed to reach its source (Stage A).</summary>
    public int ConsecutiveUnknownCount { get; set; }

    /// <summary>
    /// Anti-flapping counter: how many consecutive cycles the "raw" Stage B
    /// result has differed from the current confirmed Outcome.
    /// A transition to a new Outcome only happens after MinConsecutiveForAlert
    /// identical "raw" results in a row.
    /// </summary>
    public int ConsecutiveBadCount { get; set; }

    /// <summary>
    /// The last "raw" (pre-anti-flapping) result seen — needed to count
    /// IDENTICAL results in a row specifically, not just any change.
    /// Null right after every confirmed transition (a new streak starts
    /// from a clean slate).
    /// </summary>
    public BackupOutcome? LastRawOutcome { get; set; }

    /// <summary>Text of the last Stage A error (for display in the UI/logs). Null if the check hasn't failed.</summary>
    public string? LastError { get; set; }

    /// <summary>
    /// A rolling window of the most recent size samples — only from cycles
    /// where Stage A succeeded. Trimmed by the service to a fixed length
    /// (MaxHistorySamples, e.g. 14) on every addition.
    /// </summary>
    public List<BackupSample> History { get; init; } = new();
}
