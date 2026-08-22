namespace AdminConsole.Infrastructure.Data.Entities;

/// <summary>
/// Internal flag marking "the one-time migration from JSON has already run".
/// AdminConsole.Migration checks this before writing, so a repeat run doesn't
/// duplicate data (Phase 2, T2.6). An infrastructure entity — it has no
/// business meaning beyond persistence, so it doesn't live in AdminConsole.Domain.
/// </summary>
public sealed class MigrationMarker
{
    public int Id { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
}
