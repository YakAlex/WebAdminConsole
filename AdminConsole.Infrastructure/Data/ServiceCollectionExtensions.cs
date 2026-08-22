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
    /// PRAGMA journal_mode=WAL і PRAGMA busy_timeout виконуються через
    /// SqliteConnection.StateChange — тобто при КОЖНОМУ фізичному відкритті
    /// з'єднання, а не одноразово при старті процесу (T2.2). Дешева
    /// ідемпотентна операція; безпечніше так, ніж покладатись на те, що
    /// WAL-режим "уже стоїть" з минулого запуску.
    ///
    /// Аудит Зона 2 (2026-08-22): WAL сам по собі НЕ гарантує відсутність
    /// SQLITE_BUSY при справжньому одночасному записі — без busy_timeout
    /// "програвша" транзакція отримувала б помилку МИТТЄВО замість того,
    /// щоб почекати на writer-лок. AppLogEntries — найгарячіша таблиця в
    /// системі (кожен з 7 BackgroundServices + обидва Hangfire-джоби + REST
    /// логують туди через AppLogPersistenceHandler, кожен виклик — окреме
    /// з'єднання/транзакція) — саме тут конкурентний запис найреалістичніший.
    /// 5с — достатньо, щоб пережити типовий короткий сплеск записів, і
    /// достатньо коротко, щоб не ховати справжній deadlock надовго.
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
