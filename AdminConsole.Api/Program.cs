using AdminConsole.Api.Hubs;
using AdminConsole.Api.Security;
using AdminConsole.Domain.Abstractions;
using AdminConsole.Domain.Events;
using AdminConsole.Domain.Models;
using AdminConsole.Infrastructure.Configuration;
using AdminConsole.Infrastructure.Data;
using AdminConsole.Infrastructure.Monitoring;
using AdminConsole.Infrastructure.Remote;
using AdminConsole.Infrastructure.Reports;
using AdminConsole.Infrastructure.Security;
using AdminConsole.Infrastructure.Telegram;
using AdminConsole.Infrastructure.Zabbix;
using Hangfire;
using Hangfire.Storage.SQLite;
using MediatR;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

// Фаза 7, T7.3: Windows Service Control Manager стартує процес із
// Environment.CurrentDirectory = C:\Windows\System32 (не з теки exe).
//
// ВАЖЛИВИЙ НЮАНС (перевірено емпірично на тестовому publish): встановлення
// ContentRootPath у WebApplicationOptions виправляє ЛИШЕ ASP.NET Core
// власну файлову абстракцію (WebRootPath/wwwroot, конфіг-провайдери
// appsettings.json) — воно НЕ змінює Environment.CurrentDirectory на рівні
// процесу. А "Data Source=adminconsole.db" (EF Core SQLite),
// "hangfire.db" (Hangfire.Storage.SQLite) і "./keys" (Data Protection)
// — усі це бібліотеки поза ASP.NET Core, які резолвлять відносні шляхи
// проти Environment.CurrentDirectory напряму. Без явного
// Directory.SetCurrentDirectory ці три компоненти й далі шукали б файли
// в System32 і падали б з "Could not open database file" при першому
// зверненні до Hangfire/AdminConsoleDb — саме так це і зламалось при
// першому тестовому запуску published .exe з CWD=System32.
//
// AppContext.BaseDirectory — фактична тека, де лежить AdminConsole.Api.exe,
// коректна і під Windows Service, і під `dotnet run`/IIS Express.
Directory.SetCurrentDirectory(AppContext.BaseDirectory);

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args            = args,
    ContentRootPath = AppContext.BaseDirectory,
});

// ── T3.1: self-hosted Kestrel всередині Windows Service ─────────────────────
// Прив'язка лише до intranet-інтерфейсу (не 0.0.0.0) виконується на реальному
// деплої (Фаза 7), коли відомий IP цільового сервера. Локально порт керується
// launchSettings.json/ASPNETCORE_URLS (localhost) — безпечно за замовчуванням.
builder.Host.UseWindowsService();

// Аудит Зона 1 (2026-08-22): за замовчуванням у .NET 6+ необроблений виняток
// із ExecuteAsync будь-якого BackgroundService зупиняє ВЕСЬ хост
// (HostOptions.BackgroundServiceExceptionBehavior.StopHost) — один зламаний
// сервіс (RDP/Zabbix/Maintenance/...) кладе весь застосунок. Кожен сервіс
// нижче тепер має власний top-level try/catch (перша лінія захисту) — це
// налаштування лише страхувальна сітка про всяк випадок, якщо десь
// лишився необхоплений шлях.
builder.Services.Configure<HostOptions>(options =>
    options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);

// ── T3.2: Windows Integrated Authentication (Negotiate), без IIS ────────────
builder.Services
    .AddAuthentication(NegotiateDefaults.AuthenticationScheme)
    .AddNegotiate();

var viewerGroup = builder.Configuration["Authorization:ViewerGroup"]
    ?? throw new InvalidOperationException("Authorization:ViewerGroup не сконфігуровано.");

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("Viewer", policy => policy.RequireRole(viewerGroup));
});

// ── T3.4: мапінг AD-групи "AdminConsole-Admins" у claims (R4) ───────────────
builder.Services.AddTransient<IClaimsTransformation, WindowsGroupClaimsTransformation>();

// ── T3.3: Data Protection — шифрування ключів at-rest (DPAPI-NG) ────────────
var keyPath = builder.Configuration["DataProtection:KeyPath"]
    ?? throw new InvalidOperationException("DataProtection:KeyPath не сконфігуровано.");
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keyPath))
    .ProtectKeysWithDpapiNG()
    .SetApplicationName("AdminConsole");

// ── Домен: EF Core + репозиторії + WAL (Фаза 2) ──────────────────────────────
var connectionString = builder.Configuration.GetConnectionString("AdminConsoleDb")
    ?? throw new InvalidOperationException("ConnectionStrings:AdminConsoleDb не сконфігуровано.");
builder.Services.AddAdminConsoleDb(connectionString);

// ── T3.5: Hangfire — ОКРЕМИЙ файл БД (hangfire.db), не змішувати з доменними даними ──
// UseSQLiteStorage приймає ГОЛЕ ім'я/шлях файлу, а НЕ ADO.NET connection
// string — сам будує підключення всередині. Передача "Data Source=...;
// Cache=Shared" сюди мовчки створює файл із таким буквальним ім'ям
// (перевірено емпірично при першому запуску, виправлено до Фази 4).
var hangfireDbPath = builder.Configuration["Hangfire:SqliteDbPath"]
    ?? throw new InvalidOperationException("Hangfire:SqliteDbPath не сконфігуровано.");
builder.Services.AddHangfire(config => config
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UseSQLiteStorage(hangfireDbPath));
builder.Services.AddHangfireServer();

// ── T3.6: MediatR — заміна CommunityToolkit.Mvvm.Messaging.IMessenger ───────
// Сканується ЛИШЕ збірка Api (SignalRBroadcastHandler, T3.8). Infrastructure
// свідомо НЕ скануємо: PingMonitorService/UptimeTrackerService/RdpMonitorService/
// ZabbixPollerService одночасно є і BackgroundService (Singleton), і
// INotificationHandler<T> — якби MediatR сам знайшов їх через сканування
// збірки, він зареєстрував би їх Transient і створював НОВІ, порожні
// екземпляри на кожен Publish, відмінні від справжнього Singleton, що
// реально працює як фоновий цикл (Handle() виконувався б на "мертвому"
// об'єкті, що ніколи не бачить реальних даних). Тому кожен Infrastructure-
// хендлер нижче реєструється вручну через sp.GetRequiredService<X>(),
// що гарантовано повертає той самий Singleton.
builder.Services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(Program).Assembly));

// ── T3.7: SignalR hub ────────────────────────────────────────────────────────
builder.Services.AddSignalR();

// ── T3.9: REST-контролери ────────────────────────────────────────────────────
builder.Services.AddControllers();

// ── Конфігурація (Servers/Monitoring/BackupChecks — той самий формат appsettings.json, що й у старому WPF) ──
builder.Services.Configure<List<ServerEntry>>(builder.Configuration.GetSection("Servers"));
builder.Services.Configure<MonitoringSettings>(builder.Configuration.GetSection(MonitoringSettings.SectionName));
builder.Services.Configure<List<BackupCheckDefinition>>(builder.Configuration.GetSection("BackupChecks"));

// ── Фаза 4: перенесення моніторингових сервісів ──────────────────────────────

// T4.1 — MaintenanceService МІГРУЄ ПЕРШИМ: Ping/Uptime залежать від нього
// вже готового (StartAsync await'ить завантаження з БД до старту ExecuteAsync,
// а Generic Host гарантовано await'ить StartAsync кожного IHostedService
// у порядку реєстрації, перш ніж перейти до наступного).
builder.Services.AddSingleton<MaintenanceService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MaintenanceService>());

// T4.3 — UptimeTrackerService: другий у порядку (має бути готовий і
// зареєстрований як handler ДО того, як Ping почне публікувати).
builder.Services.AddSingleton<UptimeTrackerService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<UptimeTrackerService>());
builder.Services.AddSingleton<INotificationHandler<PingBatchResultOccurred>>(
    sp => sp.GetRequiredService<UptimeTrackerService>());
builder.Services.AddSingleton<INotificationHandler<MaintenanceChangedOccurred>>(
    sp => sp.GetRequiredService<UptimeTrackerService>());

// T4.2 — PingMonitorService: третій (dual-loop, БЕЗ Hangfire).
builder.Services.AddSingleton<PingMonitorService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<PingMonitorService>());
builder.Services.AddSingleton<INotificationHandler<MaintenanceChangedOccurred>>(
    sp => sp.GetRequiredService<PingMonitorService>());

// T4.7 — EventLogService (BackgroundService) + WinEventLogReader (static, без DI) + RemoteEventLogService (on-demand).
builder.Services.AddSingleton<EventLogService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<EventLogService>());
builder.Services.AddSingleton<RemoteEventLogService>();

// T4.9 — Remote-management: on-demand, без ExecuteAsync-циклу (навантаження
// WMI лише для вузла, що переглядається). ResourceMonitorService/
// RemoteResourceService (System Resources) прибрано повністю 2026-08-22 —
// фронтенд-вкладка Resources більше не існує.
builder.Services.AddSingleton<RemoteManagementService>();

// T5.1 — CredentialStore: секрети (Zabbix/Telegram) через Data Protection
// API (IDataProtectionProvider, зареєстрований вище T3.3) + StoredCredentials
// (Фаза 2, SQLite). Win32 Credential Manager як backing store прибрано
// повністю — RDP більше не потребує окремих credentials узагалі (сервіс
// працює під DOMAIN\svc_adminconsole, quser.exe відпрацьовує через Kerberos).
builder.Services.AddSingleton<CredentialStore>();

// T4.8 — RDP. Без CredentialsChangedOccurred-підписки — RDP credentials
// як концепція більше не існує (Kerberos, виділений service account).
builder.Services.AddSingleton<RdpMonitorService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RdpMonitorService>());
builder.Services.AddSingleton<INotificationHandler<MonitoringToggledOccurred>>(
    sp => sp.GetRequiredService<RdpMonitorService>());

// T4.10/T4.11 — Zabbix.
builder.Services.AddHttpClient<ZabbixApiClient>();
builder.Services.AddSingleton<ZabbixPollerService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ZabbixPollerService>());
builder.Services.AddSingleton<INotificationHandler<CredentialsChangedOccurred>>(
    sp => sp.GetRequiredService<ZabbixPollerService>());
builder.Services.AddSingleton<INotificationHandler<MonitoringToggledOccurred>>(
    sp => sp.GetRequiredService<ZabbixPollerService>());

// T4.4/T4.5 — BackupCheckEvaluator (чиста логіка) + BackupMonitorJob (Hangfire,
// НЕ Singleton — Scoped, бо Hangfire створює новий екземпляр на кожен запуск).
builder.Services.AddSingleton<BackupCheckEvaluator>();
builder.Services.AddScoped<BackupMonitorJob>();

// T4.12 — SLA: on-demand сервіс (Singleton, той самий екземпляр UptimeTrackerService)
// + Hangfire job за розкладом.
builder.Services.AddSingleton<SlaReportService>();
builder.Services.AddScoped<SlaReportJob>();

// T5.3 — Telegram: TelegramAccessControlService (Singleton, стан кешується
// в пам'яті, InitializeAsync викликається з TelegramBotService.ExecuteAsync)
// + TelegramBotService (BackgroundService у ТОМУ САМОМУ процесі — не окремий
// сервіс, не HTTP-клієнт до власного API; команди бота викликають
// GetSnapshot()/GetActiveWindows() НАПРЯМУ з Singleton-сервісів Фази 4).
builder.Services.AddSingleton<TelegramAccessControlService>();
builder.Services.AddSingleton<TelegramBotService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TelegramBotService>());
builder.Services.AddSingleton<INotificationHandler<PingBatchResultOccurred>>(
    sp => sp.GetRequiredService<TelegramBotService>());
builder.Services.AddSingleton<INotificationHandler<UptimeUpdatedOccurred>>(
    sp => sp.GetRequiredService<TelegramBotService>());
builder.Services.AddSingleton<INotificationHandler<RdpSessionsUpdatedOccurred>>(
    sp => sp.GetRequiredService<TelegramBotService>());
builder.Services.AddSingleton<INotificationHandler<CredentialsChangedOccurred>>(
    sp => sp.GetRequiredService<TelegramBotService>());
builder.Services.AddSingleton<INotificationHandler<BackupTransitionOccurred>>(
    sp => sp.GetRequiredService<TelegramBotService>());

// T4.13 — заміна FileLoggerService: пише AppLogEntryOccurred у БД.
// Singleton, НЕ Scoped: IMediator, впроваджений у Singleton-сервіси
// (UptimeTrackerService тощо), захоплює root-провайдер при Publish — жоден
// хендлер, якого він резолвить, не може бути Scoped. AppLogPersistenceHandler
// сам створює короткоживучий scope на кожен виклик (IServiceScopeFactory).
builder.Services.AddSingleton<INotificationHandler<AppLogEntryOccurred>, AppLogPersistenceHandler>();

var app = builder.Build();

// ── Hangfire recurring jobs (T4.5, T4.12) ────────────────────────────────────
// IRecurringJobManager (сервісна DI-based API), НЕ статичний RecurringJob —
// останній читає JobStorage.Current, який ніколи не ініціалізується, коли
// Hangfire налаштований лише через AddHangfire()/DI (а не через застарілий
// GlobalConfiguration.Configuration) — падає з "JobStorage не ініціалізовано".
{
    using var scope = app.Services.CreateScope();
    var monitoringSettings = scope.ServiceProvider.GetRequiredService<IOptions<MonitoringSettings>>().Value;
    var recurringJobs = scope.ServiceProvider.GetRequiredService<IRecurringJobManager>();

    recurringJobs.AddOrUpdate<BackupMonitorJob>(
        "backup-monitor",
        job => job.RunAsync(CancellationToken.None),
        EveryNMinutesCron(monitoringSettings.BackupPollIntervalMinutes));

    recurringJobs.AddOrUpdate<SlaReportJob>(
        "sla-report-weekly",
        job => job.RunWeeklyAsync(CancellationToken.None),
        Cron.Weekly());
}

// Крок cron-поля хвилин має бути 1-59 (Cronos відхиляє "*/60" як невалідний,
// навіть попри те що функціонально це збіглося б лише з хвилиною 0) —
// для інтервалів ≥60хв переходимо на крок по годинах замість хвилин.
static string EveryNMinutesCron(int minutes)
{
    minutes = Math.Max(minutes, 1);
    if (minutes <= 59) return $"*/{minutes} * * * *";

    int hours = Math.Clamp(minutes / 60, 1, 23);
    return hours == 1 ? "0 * * * *" : $"0 */{hours} * * *";
}

// Лише для локальної розробки: створює/оновлює схему на порожній dev-БД,
// щоб `dotnet run` одразу працював без ручного кроку. Продакшн-цикл
// лишається за AdminConsole.Migration (одноразовий перенос при cutover,
// Фаза 8) — це не заміняє його, а лише знімає тертя для локального
// тестування ендпоінтів.
if (app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<AdminConsoleDbContext>().Database.MigrateAsync();
}
else
{
    // Аудит Зона 2, Знахідка №3 (2026-08-22): production свідомо НЕ мігрує
    // сам (AdminConsole.Migration.exe — окремий ручний крок при деплої,
    // README → Deployment) — намагатись автоматично ALTER TABLE на бойовій
    // базі без відома оператора теж ризиковано. Але пропущений цей крок
    // раніше проявлявся як непрозорий SqliteException ("no such column")
    // десь усередині першого-ліпшого фонового сервісу, що торкнувся нової
    // колонки (саме так і сталось із RdpDailyPeak). Явна перевірка тут дає
    // читабельне повідомлення одразу при старті замість цього.
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AdminConsoleDbContext>();
    var pending = (await db.Database.GetPendingMigrationsAsync()).ToList();
    if (pending.Count > 0)
    {
        throw new InvalidOperationException(
            $"База даних потребує {pending.Count} незастосован(а/і) міграці(я/й): " +
            $"{string.Join(", ", pending)}. Запустіть AdminConsole.Migration.exe " +
            "(поруч із цим .exe) перед стартом AdminConsole.Api — див. README.md → Deployment / Setup.");
    }
}

app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHub<DashboardHub>("/hubs/dashboard");

// ── T3.10: SPA fallback — СТРОГО останнім, інакше перехопить /api та /hubs ──
app.MapFallbackToFile("index.html");

app.Run();
