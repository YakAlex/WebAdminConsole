namespace AdminConsole.Domain.Models;

/// <summary>
/// Result of a single backup check (server + Full/Diff type).
///
/// Unknown is only produced when the check itself failed to "look"
/// (unreachable share, UnauthorizedAccessException, timeout) — never when
/// the check ran successfully but found no file.
/// This distinction is deliberate: Missing means an honest "no backup",
/// Unknown means an honest "I don't know", and treating them the same is a
/// direct path to false alarms and eroded trust in the alerts.
/// </summary>
public enum BackupOutcome
{
    /// <summary>The check failed to reach the data source (Stage A).</summary>
    Unknown,

    /// <summary>Backup is fresh; size is within normal range, or there isn't enough history yet for an honest size estimate.</summary>
    Ok,

    /// <summary>Backup is fresh, but its size deviated from the history average beyond the threshold.</summary>
    SizeWarning,

    /// <summary>A file was found, but it's older than the allowed age.</summary>
    Stale,

    /// <summary>The data source is reachable, but no file matching the pattern was found.</summary>
    Missing
}

/// <summary>Backup type — determines which baseline the size is compared against.</summary>
public enum BackupKind
{
    Full,
    Diff
}
