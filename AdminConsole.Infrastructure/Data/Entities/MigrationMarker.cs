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
