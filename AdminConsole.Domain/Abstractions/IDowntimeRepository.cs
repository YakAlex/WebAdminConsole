using AdminConsole.Domain.Models;

namespace AdminConsole.Domain.Abstractions;

/// <summary>
/// Persistence for DowntimeRecord. Mirrors LoadFromDisk/SaveToDisk/
/// GetSnapshot from the WPF UptimeTrackerService (Phase 2, T2.3: "repository
/// instead of File I/O" — all the anti-flapping, reconciliation, and
/// deduplication business logic stays in the consuming service UNCHANGED;
/// this is only the lower persistence layer).
///
/// Unlike the file-based LoadFromDisk/SaveToDisk (which worked on top of
/// monthly JSON files and required manual grouping/deletion of empty files),
/// the relational model allows simple CRUD operations by natural key
/// (ServerIp, FellAt) — grouping by month was purely a file-storage
/// constraint, not a business rule.
/// </summary>
public interface IDowntimeRepository
{
    /// <summary>Loads all records (open and closed) — called from the service constructor, before subscribing to events.</summary>
    Task<IReadOnlyList<DowntimeRecord>> LoadAllAsync(CancellationToken ct = default);

    /// <summary>Insert or update by key (ServerIp, FellAt).</summary>
    Task UpsertAsync(DowntimeRecord record, CancellationToken ct = default);

    /// <summary>Deletes a single record by its natural key (DeleteRecord).</summary>
    Task DeleteAsync(string serverIp, DateTimeOffset fellAt, CancellationToken ct = default);

    /// <summary>Bulk-deletes all closed (IsResolved) records (ClearAllResolved). Returns the number deleted.</summary>
    Task<int> DeleteAllResolvedAsync(CancellationToken ct = default);
}
