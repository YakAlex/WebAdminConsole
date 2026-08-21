namespace AdminConsole.Infrastructure.Data;

/// <summary>Базовий клас для всіх репозиторіїв — SaveChanges через Polly retry-пайплайн (T2.3).</summary>
public abstract class RepositoryBase(AdminConsoleDbContext context)
{
    protected AdminConsoleDbContext Context { get; } = context;

    protected Task<int> SaveChangesAsync(CancellationToken ct) =>
        SqliteRetryPolicy.Pipeline.ExecuteAsync(
            async token => await Context.SaveChangesAsync(token), ct).AsTask();
}
