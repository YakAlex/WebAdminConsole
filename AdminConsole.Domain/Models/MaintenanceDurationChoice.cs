namespace AdminConsole.Domain.Models;

/// <summary>
/// Result of the duration choice in the Start Maintenance dialog.
/// Duration == null means "no time limit" (until manually turned off).
/// A null result from the dialog method itself (not this type, but the
/// Task result) means "the user cancelled" — not to be confused with
/// Duration == null.
/// </summary>
public sealed class MaintenanceDurationChoice
{
    public TimeSpan? Duration { get; init; }

    /// <summary>
    /// Free-form comment entered by the administrator in the dialog (optional).
    /// If empty/not entered, the caller falls back to the default reason
    /// "Scheduled maintenance" (see PingResultViewModel.ToggleMaintenance).
    /// </summary>
    public string? Comment { get; init; }
}
