namespace AdminConsole.Migration.LegacyModels;

/// <summary>
/// Mirrors the old WPF AdminConsole.Configuration.UserSettings ONLY for
/// deserializing user_settings.json. Not carried over into Domain — this is
/// purely a one-time migration source format (T2.6), not a working model.
/// </summary>
public sealed class LegacyUserSettings
{
    /// <summary>Deliberately ignored when mapping to AppSettings — makes no sense on the server (a Windows Service has no tray).</summary>
    public bool CloseToTray { get; set; } = true;

    public bool RdpMonitoringEnabled { get; set; } = true;
    public bool ZabbixMonitoringEnabled { get; set; } = true;
    public bool BackupMonitoringEnabled { get; set; } = true;
    public long? TelegramPrimaryAdminChatId { get; set; }
    public List<long> TelegramAllowedChatIds { get; set; } = new();
    public Dictionary<long, string> TelegramUsernames { get; set; } = new();
}
