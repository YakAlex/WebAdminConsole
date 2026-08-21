using AdminConsole.Infrastructure.Data;
using AdminConsole.Migration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// Аргументи (усі опціональні, з розумними дефолтами для локального запуску
// поруч зі старою WPF-інсталяцією):
//   [0] connection string   (дефолт: Data Source=adminconsole.db;Cache=Shared)
//   [1] стара logs-директорія (дефолт: E:\AdminConsole_v2\logs)
//   [2] старий user_settings.json (дефолт: %LocalAppData%\AdminConsole\user_settings.json)
var connectionString = args.Length > 0 ? args[0] : "Data Source=adminconsole.db;Cache=Shared";
var oldLogsDirectory  = args.Length > 1 ? args[1] : @"E:\AdminConsole_v2\logs";
var oldUserSettingsPath = args.Length > 2
    ? args[2]
    : Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AdminConsole", "user_settings.json");

var services = new ServiceCollection();
services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Information));
services.AddAdminConsoleDb(connectionString);
services.AddScoped<MigrationRunner>();

await using var provider = services.BuildServiceProvider();
await using var scope = provider.CreateAsyncScope();

var db = scope.ServiceProvider.GetRequiredService<AdminConsoleDbContext>();
await db.Database.MigrateAsync();

var runner = scope.ServiceProvider.GetRequiredService<MigrationRunner>();
var summary = await runner.RunAsync(new MigrationOptions
{
    OldLogsDirectory    = oldLogsDirectory,
    OldUserSettingsPath = oldUserSettingsPath
});

Console.WriteLine(summary.AlreadyCompleted
    ? "Міграція вже виконана раніше — нічого не зроблено (ідемпотентність, MigrationMarker)."
    : $"Готово: {summary.DowntimeRecords} downtime-записів, {summary.MaintenanceWindows} maintenance-вікон, " +
      $"{summary.BackupCheckStates} backup-станів, налаштування перенесено={summary.AppSettingsMigrated}, " +
      $"{summary.TelegramAllowedUsers} telegram-користувач(ів).");
