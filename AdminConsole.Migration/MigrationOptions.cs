namespace AdminConsole.Migration;

/// <summary>Джерельні шляхи старої WPF-інсталяції (Фаза 2, T2.6).</summary>
public sealed class MigrationOptions
{
    /// <summary>Директорія зі старими uptime-*.json / backups.json / maintenance.json (E:\AdminConsole_v2\logs).</summary>
    public required string OldLogsDirectory { get; init; }

    /// <summary>Шлях до старого %LocalAppData%\AdminConsole\user_settings.json.</summary>
    public required string OldUserSettingsPath { get; init; }
}
