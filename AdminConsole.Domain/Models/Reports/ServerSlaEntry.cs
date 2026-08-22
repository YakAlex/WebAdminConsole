namespace AdminConsole.Domain.Models.Reports;

public sealed class ServerSlaEntry
{
    public required string ServerName  { get; init; }
    public required string ServerIp    { get; init; }
    public required string ServerGroup { get; init; }
    public required bool   IsRemovedFromMonitoring { get; init; }

    public required double    UptimePercent               { get; init; } // 0..100
    public required TimeSpan  DowntimeInPeriod             { get; init; } // excluding maintenance
    public required TimeSpan  MaintenanceDowntimeInPeriod  { get; init; }
    public required int       IncidentCount                { get; init; } // excluding maintenance
    public required TimeSpan? Mttr                         { get; init; } // null if 0 closed

    public required IReadOnlyList<IncidentDetail> Incidents { get; init; }
}
