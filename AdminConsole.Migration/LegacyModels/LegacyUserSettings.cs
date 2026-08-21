namespace AdminConsole.Migration.LegacyModels;

/// <summary>
/// Дзеркалить стару WPF AdminConsole.Configuration.UserSettings ЛИШЕ для
/// десеріалізації user_settings.json. Не переноситься в Domain — це суто
/// одноразовий формат джерела міграції (T2.6), а не робоча модель.
/// </summary>
public sealed class LegacyUserSettings
{
    /// <summary>Навмисно ігнорується при мапінгу в AppSettings — немає сенсу на сервері (Windows Service без трею).</summary>
    public bool CloseToTray { get; set; } = true;

    public bool RdpMonitoringEnabled { get; set; } = true;
    public bool ZabbixMonitoringEnabled { get; set; } = true;
    public bool BackupMonitoringEnabled { get; set; } = true;
    public long? TelegramPrimaryAdminChatId { get; set; }
    public List<long> TelegramAllowedChatIds { get; set; } = new();
    public Dictionary<long, string> TelegramUsernames { get; set; } = new();
}
