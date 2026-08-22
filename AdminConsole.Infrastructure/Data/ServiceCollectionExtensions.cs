using System.Data;
using AdminConsole.Domain.Abstractions;
using AdminConsole.Infrastructure.Data.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AdminConsole.Infrastructure.Data;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers AdminConsoleDbContext + all repositories.
    ///
    /// PRAGMA journal_mode=WAL and PRAGMA busy_timeout are executed via
    /// SqliteConnection.StateChange — meaning on EVERY physical connection
    /// open, not once at process startup (T2.2). It's a cheap, idempotent
    /// operation; safer than relying on the WAL mode "already being set"
    /// from a previous run.
    ///
    /// Zone 2 audit (2026-08-22): WAL by itself does NOT guarantee the
    /// absence of SQLITE_BUSY under genuinely concurrent writes — without
    /// busy_timeout, the "losing" transaction would get an error IMMEDIATELY
    /// instead of waiting for the writer lock. AppLogEntries is the hottest
    /// table in the system (each of the 7 BackgroundServices + both Hangfire
    /// jobs + REST all log to it through AppLogPersistenceHandler, each call
    /// being a separate connection/transaction) — this is where concurrent
    /// writes are most realistic. 5s is enough to ride out a typical short
    /// burst of writes, and short enough not to hide a real deadlock for long.
    /// </summary>
    public static IServiceCollection AddAdminConsoleDb(
        this IServiceCollection services, string connectionString)
    {
        services.AddDbContext<AdminConsoleDbContext>(options =>
        {
            var connection = new SqliteConnection(connectionString);
            connection.StateChange += (_, e) =>
            {
                if (e.CurrentState != ConnectionState.Open) return;
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
                cmd.ExecuteNonQuery();
            };
            options.UseSqlite(connection);
        });

        services.AddScoped<IDowntimeRepository, DowntimeRepository>();
        services.AddScoped<IMaintenanceRepository, MaintenanceRepository>();
        services.AddScoped<IBackupStateRepository, BackupStateRepository>();
        services.AddScoped<IAppSettingsRepository, AppSettingsRepository>();
        services.AddScoped<IAppLogRepository, AppLogRepository>();
        services.AddScoped<ICredentialRepository, CredentialRepository>();

        return services;
    }
}
