using AdminConsole.Domain.Models;
using AdminConsole.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace AdminConsole.Infrastructure.Data;

public sealed class AdminConsoleDbContext(DbContextOptions<AdminConsoleDbContext> options)
    : DbContext(options)
{
    public DbSet<DowntimeRecord>       DowntimeRecords      => Set<DowntimeRecord>();
    public DbSet<MaintenanceWindow>    MaintenanceWindows   => Set<MaintenanceWindow>();
    public DbSet<BackupCheckState>     BackupCheckStates    => Set<BackupCheckState>();
    public DbSet<AppSettings>          AppSettings          => Set<AppSettings>();
    public DbSet<TelegramAllowedUser>  TelegramAllowedUsers => Set<TelegramAllowedUser>();
    public DbSet<AppLogEntry>          AppLogEntries        => Set<AppLogEntry>();
    public DbSet<MigrationMarker>      MigrationMarkers     => Set<MigrationMarker>();
    public DbSet<StoredCredential>     StoredCredentials    => Set<StoredCredential>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AdminConsoleDbContext).Assembly);
    }
}
