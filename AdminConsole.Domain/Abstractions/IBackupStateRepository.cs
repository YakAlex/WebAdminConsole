using AdminConsole.Domain.Models;

namespace AdminConsole.Domain.Abstractions;

/// <summary>
/// Persistence for BackupCheckState (+ its nested History of BackupSample).
/// Mirrors LoadFromDisk/SaveToDisk/GetSnapshot from the WPF BackupMonitorService
/// (Phase 2, T2.3). The record key is (Name, Kind), the same StateKey the
/// service already uses for deduplication/lookup.
///
/// The Infrastructure implementation is responsible for correctly persisting
/// the nested History (1→N, FK BackupCheckStateId, trimmed to MaxHistorySamples —
/// that limit remains the consuming service's responsibility, not the
/// repository's).
/// </summary>
public interface IBackupStateRepository
{
    /// <summary>Loads all backup check states.</summary>
    Task<IReadOnlyList<BackupCheckState>> LoadAllAsync(CancellationToken ct = default);

    /// <summary>Insert or update by key (Name, Kind), including History.</summary>
    Task UpsertAsync(BackupCheckState state, CancellationToken ct = default);

    /// <summary>Deletes stale records whose keys are no longer part of the current BackupChecks configuration.</summary>
    Task<int> DeleteWhereKeyNotInAsync(IReadOnlySet<string> validKeys, CancellationToken ct = default);
}
