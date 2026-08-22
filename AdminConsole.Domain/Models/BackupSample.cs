namespace AdminConsole.Domain.Models;

/// <summary>
/// A single recorded backup size in the rolling history. Stores raw data
/// (not just an aggregated average) deliberately — so a future move to a
/// median/MAD approach won't require a file-format migration.
/// </summary>
public sealed class BackupSample
{
    public DateTimeOffset ObservedAt { get; init; }
    public long           SizeBytes  { get; init; }
}
