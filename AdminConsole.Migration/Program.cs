using AdminConsole.Infrastructure.Data;
using AdminConsole.Migration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// Arguments (all optional, with sensible defaults for a local run alongside
// the old WPF installation):
//   [0] connection string      (default: Data Source=adminconsole.db;Cache=Shared)
//   [1] old logs directory     (default: E:\AdminConsole_v2\logs)
//   [2] old user_settings.json (default: %LocalAppData%\AdminConsole\user_settings.json)
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
    ? "Migration was already completed earlier — nothing to do (idempotent, MigrationMarker)."
    : $"Done: {summary.DowntimeRecords} downtime record(s), {summary.MaintenanceWindows} maintenance window(s), " +
      $"{summary.BackupCheckStates} backup state(s), settings migrated={summary.AppSettingsMigrated}, " +
      $"{summary.TelegramAllowedUsers} telegram user(s).");
