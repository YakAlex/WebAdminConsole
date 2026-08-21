namespace AdminConsole.Infrastructure.Configuration;

/// <summary>
/// Bind-клас для розділу "Monitoring" у appsettings.json. Options-конфігурація —
/// адаптерний, не доменний концепт, тому в Infrastructure, а не в Domain
/// (на відміну від ServerEntry/AppSettings, які є справжніми доменними моделями).
/// Перенесено без змін з AdminConsole.Configuration.MonitoringSettings.
/// </summary>
public sealed class MonitoringSettings
{
    public const string SectionName = "Monitoring";

    public int    PingIntervalSeconds              { get; init; } = 30;

    /// <summary>
    /// Інтервал пінгу для серверів у стані Offline.
    /// Recovery loop пінгує тільки їх — швидше виявляє відновлення.
    /// Має бути менше PingIntervalSeconds. Мінімум 5с.
    /// </summary>
    public int    OfflinePingIntervalSeconds       { get; init; } = 10;
    public string ZabbixUrl                        { get; init; } = string.Empty;
    public int    ZabbixPollIntervalSeconds        { get; init; } = 60;
    public int    RdpPollIntervalSeconds           { get; init; } = 120;
    public int    LocalResourcePollIntervalSeconds { get; init; } = 3;

    /// <summary>
    /// Мінімальна тривалість (у секундах) даунтайму, щоб він потрапив
    /// у DowntimeRecord і зберігся в БД. Коротші "миготіння"
    /// (наприклад, одиничний втрачений ping-пакет через мережеву затримку)
    /// відкидаються при відновленні і не рахуються як SLA-інцидент.
    /// 0 — вимкнути фільтр (записувати все, як раніше).
    /// </summary>
    public int MinIncidentDurationSeconds { get; init; } = 10;

    /// <summary>Інтервал опитування BackupMonitorJob. Бекапи не змінюються щохвилини — дефолт навмисно більший за решту.</summary>
    public int BackupPollIntervalMinutes { get; init; } = 60;
}
