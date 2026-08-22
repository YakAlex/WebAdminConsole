using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AdminConsole.Infrastructure.Data;

/// <summary>
/// Allows `dotnet ef migrations add` to work without a fully bootstrapped
/// Api host (Program.cs isn't configured yet in Phase 2 — the DI graph for
/// AddDbContext won't appear until Phase 3). Design-time-only wiring,
/// never used at the application's actual runtime.
/// </summary>
public sealed class AdminConsoleDbContextFactory : IDesignTimeDbContextFactory<AdminConsoleDbContext>
{
    public AdminConsoleDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<AdminConsoleDbContext>();
        optionsBuilder.UseSqlite("Data Source=adminconsole.db");
        return new AdminConsoleDbContext(optionsBuilder.Options);
    }
}
