namespace AdminConsole.Domain.Models;
using System.Text.Json.Serialization;

/// <summary>
/// A scheduled maintenance window for a server or a group of servers.
/// While active (From..To), pollers do not raise Warning/Error about this
/// server being unreachable, and UptimeTracker does not count it as an
/// SLA incident.
///
/// Key in MaintenanceService: ServerIp if TargetGroup == null, otherwise
/// "group:{TargetGroup}". One active window per key.
/// </summary>
public sealed class MaintenanceWindow
{
    /// <summary>IP of a specific server. Null if the window applies to a whole group.</summary>
    public string? ServerIp { get; init; }

    /// <summary>Group name (ServerEntry.Group). Null if the window applies to a single server.</summary>
    public string? TargetGroup { get; init; }

    /// <summary>Display name for the UI/logs (server or group name).</summary>
    public required string DisplayName { get; init; }

    public required DateTimeOffset From { get; init; }

    /// <summary>
    /// The moment the window ends automatically. Null means "no time limit":
    /// the window stays active until the administrator turns it off
    /// manually (EndMaintenanceEarly) — MaintenanceService's background
    /// auto-completion cycle simply skips such windows when checking for
    /// expiration.
    /// </summary>
    public DateTimeOffset? To { get; init; }

    public string Reason { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;

    public bool IsActiveAt(DateTimeOffset now) => now >= From && (To is null || now <= To);

    /// <summary>Key used for storage/lookup in MaintenanceService.</summary>
    [JsonIgnore]
    public string Key => TargetGroup is not null
        ? $"group:{TargetGroup}"
        : ServerIp ?? throw new InvalidOperationException(
            "MaintenanceWindow must have either ServerIp or TargetGroup.");
}
