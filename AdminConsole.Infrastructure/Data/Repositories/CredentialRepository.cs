using AdminConsole.Domain.Abstractions;
using AdminConsole.Domain.Models;
using Microsoft.EntityFrameworkCore;

namespace AdminConsole.Infrastructure.Data.Repositories;

public sealed class CredentialRepository(AdminConsoleDbContext context)
    : RepositoryBase(context), ICredentialRepository
{
    public async Task<StoredCredential?> GetAsync(string target, CancellationToken ct = default) =>
        await Context.StoredCredentials.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Target == target, ct);

    public async Task UpsertAsync(StoredCredential credential, CancellationToken ct = default)
    {
        var existing = await Context.StoredCredentials.FindAsync([credential.Target], ct);
        if (existing is null)
        {
            Context.StoredCredentials.Add(credential);
        }
        else
        {
            existing.Username        = credential.Username;
            existing.ProtectedSecret = credential.ProtectedSecret;
        }

        await SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(string target, CancellationToken ct = default)
    {
        var existing = await Context.StoredCredentials.FindAsync([target], ct);
        if (existing is null) return;

        Context.StoredCredentials.Remove(existing);
        await SaveChangesAsync(ct);
    }
}
