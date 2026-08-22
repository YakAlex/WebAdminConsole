namespace AdminConsole.Domain.Models;

/// <summary>A single entry from the "BackupChecks" array in appsettings.json.</summary>
public sealed class BackupCheckDefinition
{
    public string Name { get; init; } = string.Empty;

    public string Host { get; init; } = string.Empty;

    /// <summary>Local or UNC path to the folder containing backup files.</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>Glob pattern for full backup file names (e.g. "*_full_*.bak").</summary>
    public string FullPattern { get; init; } = string.Empty;

    /// <summary>
    /// Glob pattern for diff/incremental backup file names.
    /// An empty string means Diff is not checked for this server.
    /// </summary>
    public string DiffPattern { get; init; } = string.Empty;

    public int MaxAgeHoursFull { get; init; } = 26;
    public int MaxAgeHoursDiff { get; init; } = 26;

    /// <summary>
    /// Threshold (in percent) for how far the current size may deviate
    /// from the History average before SizeWarning is raised.
    /// </summary>
    public int SizeWarningThresholdPct { get; init; } = 30;

    /// <summary>
    /// Minimum number of samples in History before size is evaluated at
    /// all. Before that, only age is checked, with status Ok.
    /// </summary>
    public int MinSamplesForBaseline { get; init; } = 3;

    /// <summary>How many consecutive cycles a "raw" result must hold before it becomes confirmed (anti-flapping).</summary>
    public int MinConsecutiveForAlert { get; init; } = 2;
}
