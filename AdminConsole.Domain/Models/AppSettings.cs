namespace AdminConsole.Domain.Models;

/// <summary>
/// Persistent application settings (single-row AppSettings table,
/// Phase 2). Replaces the WPF UserSettings (%LocalAppData%\...\user_settings.json)
/// MINUS CloseToTray — that field makes no sense on a server (a Windows
/// Service has no system tray).
/// </summary>
public sealed class AppSettings
{
    /// <summary>
    /// true  — RdpMonitorService polls Terminal Servers.
    /// false — the service makes no quser calls and does not prompt for
    ///         RDP credentials, even if they are missing (checked BEFORE
    ///         any credential logic).
    /// </summary>
    public bool RdpMonitoringEnabled { get; set; } = true;

    /// <summary>
    /// true  — ZabbixPollerService polls the Zabbix API.
    /// false — the service makes no requests and does not prompt for a
    ///         Zabbix token, even if one is missing (checked BEFORE any
    ///         credential logic).
    /// </summary>
    public bool ZabbixMonitoringEnabled { get; set; } = true;

    /// <summary>
    /// true  — BackupMonitorService runs the BackupChecks.
    /// false — the service performs no file/network I/O against
    ///         BackupChecks paths, even if they are configured (checked
    ///         BEFORE any filesystem access).
    /// </summary>
    public bool BackupMonitoringEnabled { get; set; } = true;

    /// <summary>
    /// Telegram Primary Admin chat ID. Set once via /claim_admin with a
    /// code generated in Settings. Null = not yet claimed.
    /// </summary>
    public long? TelegramPrimaryAdminChatId { get; set; }

    /// <summary>
    /// The highest number of simultaneously active RDP sessions seen on
    /// the current day (RdpDailyPeakDate). Persisted because
    /// RdpMonitorService would otherwise keep the peak only in memory —
    /// a service restart (deploy, reboot) would reset "Peak today" to 0
    /// even if a session had already connected and disconnected earlier
    /// that same day.
    /// </summary>
    public int RdpDailyPeak { get; set; }

    /// <summary>The date (no time) that RdpDailyPeak applies to. A different date means a new day, and the peak resets.</summary>
    public DateTime RdpDailyPeakDate { get; set; }

    /// <summary>
    /// Minimum Zabbix severity ZabbixPollerService fetches (ZabbixSeverity enum
    /// value: 0=NotClassified .. 5=Disaster). Default 4 (High) preserves the
    /// behavior from before this setting existed (High/Disaster only).
    /// </summary>
    public int ZabbixMinSeverity { get; set; } = 4;
}
