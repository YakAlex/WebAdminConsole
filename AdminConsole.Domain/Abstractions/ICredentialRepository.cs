using AdminConsole.Domain.Models;

namespace AdminConsole.Domain.Abstractions;

/// <summary>Persistence for StoredCredential (Phase 5, T5.1).</summary>
public interface ICredentialRepository
{
    Task<StoredCredential?> GetAsync(string target, CancellationToken ct = default);
    Task UpsertAsync(StoredCredential credential, CancellationToken ct = default);
    Task DeleteAsync(string target, CancellationToken ct = default);
}
