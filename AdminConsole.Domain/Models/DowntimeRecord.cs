namespace AdminConsole.Domain.Models;
using System.Text.Json.Serialization;

public sealed class DowntimeRecord
{
    public string          ServerName   { get; init; } = string.Empty;
    public string          ServerIp     { get; init; } = string.Empty;
    public string          ServerGroup  { get; init; } = string.Empty;
    public DateTimeOffset  FellAt       { get; init; }
    public DateTimeOffset? RecoveredAt  { get; set;  }

    /// <summary>
    /// true — the incident was closed not because the server actually
    /// recovered, but because the administrator turned on Maintenance Mode
    /// while the incident was still open. Lets SLA report calculations
    /// distinguish "genuine" downtime from downtime interrupted manually.
    /// </summary>
    public bool ClosedByMaintenance { get; set; }

    [JsonIgnore]
    public TimeSpan Duration => RecoveredAt.HasValue
        ? RecoveredAt.Value - FellAt
        : DateTimeOffset.Now - FellAt;

    [JsonIgnore]
    public bool IsResolved => RecoveredAt.HasValue;

    [JsonIgnore]
    public string DurationDisplay
    {
        get
        {
            var d = Duration;
            string baseText = d.TotalHours >= 1
                ? $"{(int)d.TotalHours}h {d.Minutes:D2}m"
                : d.TotalMinutes >= 1
                    ? $"{(int)d.TotalMinutes}m {d.Seconds:D2}s"
                    : $"{d.Seconds}s";

            return ClosedByMaintenance ? $"{baseText} (maintenance)" : baseText;
        }
    }
}
