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
    /// Реєструє AdminConsoleDbContext + усі репозиторії.
    ///
    /// PRAGMA journal_mode=WAL виконується через SqliteConnection.StateChange —
    /// тобто при КОЖНОМУ фізичному відкритті з'єднання, а не одноразово при
    /// старті процесу (T2.2). Дешева ідемпотентна операція; безпечніше так,
    /// ніж покладатись на те, що WAL-режим "уже стоїть" з минулого запуску.
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
                cmd.CommandText = "PRAGMA journal_mode=WAL;";
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
