namespace AdminConsole.Domain.Models;

/// <summary>
/// Персистентні налаштування застосунку (single-row таблиця AppSettings,
/// Фаза 2). Заміна WPF UserSettings (%LocalAppData%\...\user_settings.json)
/// МІНУС CloseToTray — поле не має сенсу на сервері (Windows Service без
/// системного трею).
/// </summary>
public sealed class AppSettings
{
    /// <summary>
    /// true  — RdpMonitorService опитує Terminal Servers.
    /// false — сервіс не робить quser-запитів і не запитує RDP credentials,
    ///         навіть якщо вони відсутні (перевіряється ДО credential-логіки).
    /// </summary>
    public bool RdpMonitoringEnabled { get; set; } = true;

    /// <summary>
    /// true  — ZabbixPollerService опитує Zabbix API.
    /// false — сервіс не робить запитів і не запитує Zabbix токен,
    ///         навіть якщо він відсутній (перевіряється ДО credential-логіки).
    /// </summary>
    public bool ZabbixMonitoringEnabled { get; set; } = true;

    /// <summary>
    /// true  — BackupMonitorService виконує перевірки BackupChecks.
    /// false — сервіс не робить жодного файлового/мережевого I/O по шляхах
    ///         з BackupChecks, навіть якщо вони сконфігуровані (перевіряється
    ///         ДО будь-якого звернення до файлової системи).
    /// </summary>
    public bool BackupMonitoringEnabled { get; set; } = true;

    /// <summary>
    /// Chat ID Primary Admin в Telegram. Встановлюється один раз через
    /// /claim_admin з кодом, згенерованим у Settings. Null = ще не прив'язано.
    /// </summary>
    public long? TelegramPrimaryAdminChatId { get; set; }
}
