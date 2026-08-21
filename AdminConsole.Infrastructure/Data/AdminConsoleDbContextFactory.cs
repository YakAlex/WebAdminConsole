using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AdminConsole.Infrastructure.Data;

/// <summary>
/// Дозволяє `dotnet ef migrations add` працювати без повністю піднятого
/// Api-хоста (Program.cs ще не налаштований у Фазі 2 — DI-графа для
/// AddDbContext з'явиться лише у Фазі 3). Design-time-only підключення,
/// ніколи не використовується в реальному рантаймі застосунку.
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
