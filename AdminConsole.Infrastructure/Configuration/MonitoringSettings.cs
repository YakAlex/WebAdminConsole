namespace AdminConsole.Infrastructure.Configuration;

/// <summary>
/// Binding class for the "Monitoring" section in appsettings.json. Options
/// configuration is an adapter-level, not a domain-level concept, hence it
/// lives in Infrastructure rather than Domain (unlike ServerEntry/AppSettings,
/// which are genuine domain models). Moved over unchanged from
/// AdminConsole.Configuration.MonitoringSettings.
/// </summary>
public sealed class MonitoringSettings
{
    public const string SectionName = "Monitoring";

    public int    PingIntervalSeconds              { get; init; } = 30;

    /// <summary>
    /// Ping interval for servers in the Offline state.
    /// The recovery loop pings only these — it detects recovery faster.
    /// Must be less than PingIntervalSeconds. Minimum 5s.
    /// </summary>
    public int    OfflinePingIntervalSeconds       { get; init; } = 10;
    public string ZabbixUrl                        { get; init; } = string.Empty;
    public int    ZabbixPollIntervalSeconds        { get; init; } = 60;
    public int    RdpPollIntervalSeconds           { get; init; } = 120;

    /// <summary>
    /// Minimum downtime duration (in seconds) required for it to be recorded
    /// as a DowntimeRecord and saved to the DB. Shorter "blips"
    /// (e.g. a single lost ping packet due to network latency)
    /// are discarded on recovery and don't count as an SLA incident.
    /// 0 disables the filter (records everything, as before).
    /// </summary>
    public int MinIncidentDurationSeconds { get; init; } = 10;

    /// <summary>Polling interval for BackupMonitorJob. Backups don't change every minute — the default is deliberately larger than the others.</summary>
    public int BackupPollIntervalMinutes { get; init; } = 60;
}
