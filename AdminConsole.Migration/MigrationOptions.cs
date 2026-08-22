namespace AdminConsole.Migration;

/// <summary>Source paths from the old WPF installation (Phase 2, T2.6).</summary>
public sealed class MigrationOptions
{
    /// <summary>Directory with the old uptime-*.json / backups.json / maintenance.json files (E:\AdminConsole_v2\logs).</summary>
    public required string OldLogsDirectory { get; init; }

    /// <summary>Path to the old %LocalAppData%\AdminConsole\user_settings.json.</summary>
    public required string OldUserSettingsPath { get; init; }
}
