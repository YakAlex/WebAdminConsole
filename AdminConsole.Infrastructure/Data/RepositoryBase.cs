namespace AdminConsole.Infrastructure.Data;

/// <summary>Base class for all repositories — SaveChanges runs through the Polly retry pipeline (T2.3).</summary>
public abstract class RepositoryBase(AdminConsoleDbContext context)
{
    protected AdminConsoleDbContext Context { get; } = context;

    protected Task<int> SaveChangesAsync(CancellationToken ct) =>
        SqliteRetryPolicy.Pipeline.ExecuteAsync(
            async token => await Context.SaveChangesAsync(token), ct).AsTask();
}
