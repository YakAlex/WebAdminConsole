using AdminConsole.Domain.Models;

namespace AdminConsole.Domain.Abstractions;

/// <summary>
/// Persistence for MaintenanceWindow. Mirrors LoadFromDisk/SaveToDisk from
/// the WPF MaintenanceService (Phase 2, T2.3). The record key is
/// MaintenanceWindow.Key (ServerIp or "group:{TargetGroup}") — in the
/// relational model this becomes a real column/primary key instead of a
/// [JsonIgnore]-computed property.
/// </summary>
public interface IMaintenanceRepository
{
    /// <summary>Loads all maintenance windows (including expired ones — time-based filtering remains the service's responsibility, as it is now).</summary>
    Task<IReadOnlyList<MaintenanceWindow>> LoadAllAsync(CancellationToken ct = default);

    /// <summary>Insert or update by Key (StartMaintenance).</summary>
    Task UpsertAsync(MaintenanceWindow window, CancellationToken ct = default);

    /// <summary>Removes a single window by Key (EndMaintenanceEarly, auto-completion of expired windows).</summary>
    Task RemoveAsync(string key, CancellationToken ct = default);

    /// <summary>Removes ALL windows at once (ClearAllOnShutdown on a real application exit).</summary>
    Task RemoveAllAsync(CancellationToken ct = default);
}
