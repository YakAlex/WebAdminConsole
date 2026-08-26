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
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Threading.RateLimiting;

// Phase 7, T7.3: the Windows Service Control Manager starts the process with
// Environment.CurrentDirectory = C:\Windows\System32 (not the exe's folder).
//
// IMPORTANT NUANCE (verified empirically on a test publish): setting
// ContentRootPath in WebApplicationOptions ONLY fixes ASP.NET Core's own
// file abstraction (WebRootPath/wwwroot, appsettings.json config providers)
// — it does NOT change the process-level Environment.CurrentDirectory. And
// "Data Source=adminconsole.db" (EF Core SQLite), "hangfire.db"
// (Hangfire.Storage.SQLite), and "./keys" (Data Protection) are all
// libraries outside ASP.NET Core that resolve relative paths against
// Environment.CurrentDirectory directly. Without an explicit
// Directory.SetCurrentDirectory, these three components would keep looking
// for files in System32 and fail with "Could not open database file" on the
// first access to Hangfire/AdminConsoleDb — which is exactly what happened
// the first time the published .exe was run as a service with CWD=System32.
//
// AppContext.BaseDirectory is the actual folder containing
// AdminConsole.Api.exe — correct both under a Windows Service and under
// `dotnet run`/IIS Express.
Directory.SetCurrentDirectory(AppContext.BaseDirectory);

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args            = args,
    ContentRootPath = AppContext.BaseDirectory,
});

// ── T3.1: self-hosted Kestrel inside a Windows Service ───────────────────────
// Binding to just the intranet interface (not 0.0.0.0) happens at actual
// deployment time (Phase 7), once the target server's IP is known. Locally
// the port is controlled by launchSettings.json/ASPNETCORE_URLS (localhost)
// — a safe default.
builder.Host.UseWindowsService();

// Audit Zone 1 (2026-08-22): by default in .NET 6+, an unhandled exception
// from any BackgroundService's ExecuteAsync stops the ENTIRE host
// (HostOptions.BackgroundServiceExceptionBehavior.StopHost) — one broken
// service (RDP/Zabbix/Maintenance/...) would take down the whole app. Every
// service below now has its own top-level try/catch (first line of defense)
// — this setting is just a safety net in case an uncaught path was missed
// somewhere.
builder.Services.Configure<HostOptions>(options =>
    options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);

// ── Cookie authentication — replaces Windows Integrated Auth (Negotiate) ────
// with a custom login form that still validates against AD
// (IAdAuthenticationService/WindowsAdAuthenticationService), for a friendlier
// login UX than the native browser credentials popup Negotiate produces.
// CookieSecurePolicy.Always means the auth cookie is ONLY set/sent over
// HTTPS — this REQUIRES the Kestrel Https endpoint (appsettings.json,
// "Kestrel:Endpoints:Https") to be configured, or login will silently fail
// (the browser drops a Secure cookie sent over plain HTTP).
builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "AdminConsole.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;

        // This is an SPA/JSON API, not a server-rendered site — the default
        // CookieAuthenticationHandler redirects to LoginPath on 401/403,
        // which would return index.html (200) instead of a real status
        // code, breaking http.ts's status-code-based auth handling.
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    });

var viewerGroup = builder.Configuration["Authorization:ViewerGroup"]
    ?? throw new InvalidOperationException("Authorization:ViewerGroup is not configured.");

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("Viewer", policy => policy.RequireRole(viewerGroup));
});

// ── Login brute-force throttle ───────────────────────────────────────────────
// AD itself typically enforces an account-lockout policy after repeated bad
// passwords, but that protects the AD account, not this endpoint from being
// hammered — a simple fixed-window throttle here is a cheap, standard first
// line of defense, with no added package (built into the ASP.NET Core 8
// shared framework).
//
// Partitioned per client IP (RemoteIpAddress), NOT a single shared/global
// limiter: AddFixedWindowLimiter(name, ...) would create ONE limiter
// instance shared by every caller hitting the "login" policy, so any two
// admins mistyping a password in the same minute would lock out everyone
// else, and an unauthenticated attacker could send 5 req/min forever and
// permanently block all logins — a trivial DoS against the whole admin
// console. Partitioning by IP keeps the throttle scoped to the offending
// client.
builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("login", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
    options.OnRejected = (context, _) =>
    {
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        // Matches the 1-minute fixed window above — gives the frontend's
        // "Забагато спроб входу. Спробуйте пізніше." message something
        // concrete to act on instead of an unqualified "try later".
        context.HttpContext.Response.Headers.RetryAfter = "60";
        return ValueTask.CompletedTask;
    };
});

// ── AD access, used once at login by AuthController (Authorization:ViewerGroup group check is NOT re-run per request — see AuthController's doc comment) ──
builder.Services.AddSingleton<IAdAuthenticationService, WindowsAdAuthenticationService>();

// ── T3.3: Data Protection — encrypting keys at rest (DPAPI-NG) ──────────────
var keyPath = builder.Configuration["DataProtection:KeyPath"]
    ?? throw new InvalidOperationException("DataProtection:KeyPath is not configured.");
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keyPath))
    .ProtectKeysWithDpapiNG()
    .SetApplicationName("AdminConsole");

// ── Domain: EF Core + repositories + WAL (Phase 2) ───────────────────────────
var connectionString = builder.Configuration.GetConnectionString("AdminConsoleDb")
    ?? throw new InvalidOperationException("ConnectionStrings:AdminConsoleDb is not configured.");
builder.Services.AddAdminConsoleDb(connectionString);

// ── T3.5: Hangfire — SEPARATE database file (hangfire.db), do not mix with domain data ──
// UseSQLiteStorage takes a BARE file name/path, NOT an ADO.NET connection
// string — it builds the connection internally. Passing "Data Source=...;
// Cache=Shared" here silently creates a file with that literal name
// (verified empirically on the first run, fixed before Phase 4).
var hangfireDbPath = builder.Configuration["Hangfire:SqliteDbPath"]
    ?? throw new InvalidOperationException("Hangfire:SqliteDbPath is not configured.");
builder.Services.AddHangfire(config => config
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UseSQLiteStorage(hangfireDbPath));
builder.Services.AddHangfireServer();

// ── T3.6: MediatR — replacement for CommunityToolkit.Mvvm.Messaging.IMessenger ──
// ONLY the Api assembly is scanned (SignalRBroadcastHandler, T3.8).
// Infrastructure is deliberately NOT scanned: PingMonitorService/
// UptimeTrackerService/RdpMonitorService/ZabbixPollerService are
// simultaneously a BackgroundService (Singleton) and an
// INotificationHandler<T> — if MediatR discovered them itself via assembly
// scanning, it would register them as Transient and create NEW, empty
// instances on every Publish, distinct from the real Singleton that actually
// runs as the background loop (Handle() would run on a "dead" object that
// never sees real data). So every Infrastructure handler below is registered
// manually via sp.GetRequiredService<X>(), which is guaranteed to return the
// same Singleton.
builder.Services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(Program).Assembly));

// ── T3.7: SignalR hub ────────────────────────────────────────────────────────
builder.Services.AddSignalR();

// ── T3.9: REST controllers ───────────────────────────────────────────────────
builder.Services.AddControllers();

// ── Configuration (Servers/Monitoring/BackupChecks — same appsettings.json format as the old WPF app) ──
builder.Services.Configure<List<ServerEntry>>(builder.Configuration.GetSection("Servers"));
builder.Services.Configure<MonitoringSettings>(builder.Configuration.GetSection(MonitoringSettings.SectionName));
builder.Services.Configure<List<BackupCheckDefinition>>(builder.Configuration.GetSection("BackupChecks"));

// ── Phase 4: migrating the monitoring services ───────────────────────────────

// T4.1 — MaintenanceService MIGRATES FIRST: Ping/Uptime depend on it already
// being ready (StartAsync awaits loading from the DB before ExecuteAsync
// starts, and the Generic Host is guaranteed to await each IHostedService's
// StartAsync in registration order before moving to the next one).
builder.Services.AddSingleton<MaintenanceService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MaintenanceService>());

// T4.3 — UptimeTrackerService: second in order (must be ready and registered
// as a handler BEFORE Ping starts publishing).
builder.Services.AddSingleton<UptimeTrackerService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<UptimeTrackerService>());
builder.Services.AddSingleton<INotificationHandler<PingBatchResultOccurred>>(
    sp => sp.GetRequiredService<UptimeTrackerService>());
builder.Services.AddSingleton<INotificationHandler<MaintenanceChangedOccurred>>(
    sp => sp.GetRequiredService<UptimeTrackerService>());

// T4.2 — PingMonitorService: third (dual-loop, WITHOUT Hangfire).
builder.Services.AddSingleton<PingMonitorService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<PingMonitorService>());
builder.Services.AddSingleton<INotificationHandler<MaintenanceChangedOccurred>>(
    sp => sp.GetRequiredService<PingMonitorService>());

// T4.9 — Remote management: on-demand, without an ExecuteAsync loop (WMI
// load only for the node currently being viewed). ResourceMonitorService/
// RemoteResourceService (System Resources) was removed entirely on
// 2026-08-22 — the frontend Resources tab no longer exists.
builder.Services.AddSingleton<RemoteManagementService>();

// T5.1 — CredentialStore: secrets (Zabbix/Telegram) via the Data Protection
// API (IDataProtectionProvider, registered above in T3.3) + StoredCredentials
// (Phase 2, SQLite). The Win32 Credential Manager backing store has been
// removed entirely — RDP no longer needs separate credentials at all (the
// service runs as DOMAIN\svc_adminconsole, quser.exe authenticates via
// Kerberos).
builder.Services.AddSingleton<CredentialStore>();

// T4.8 — RDP. No CredentialsChangedOccurred subscription — RDP credentials
// no longer exist as a concept (Kerberos, dedicated service account).
builder.Services.AddSingleton<RdpMonitorService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RdpMonitorService>());
builder.Services.AddSingleton<INotificationHandler<MonitoringToggledOccurred>>(
    sp => sp.GetRequiredService<RdpMonitorService>());

// T4.10/T4.11 — Zabbix.
// Audit Zone 3, Finding #2 (2026-08-22): without an explicit Timeout, the
// default HttpClient.Timeout (100s) applied — on the REST path
// (/api/zabbix, GetActiveProblemsNowInternalAsync) this meant a hung Zabbix
// response could hold the user's HTTP request for up to 100s. Same approach
// already applied to RDP (SnapshotTimeoutMs), just via a shorter path — at
// the HttpClient level itself.
builder.Services.AddHttpClient<ZabbixApiClient>(c => c.Timeout = TimeSpan.FromSeconds(20));
builder.Services.AddSingleton<ZabbixPollerService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ZabbixPollerService>());
builder.Services.AddSingleton<INotificationHandler<CredentialsChangedOccurred>>(
    sp => sp.GetRequiredService<ZabbixPollerService>());
builder.Services.AddSingleton<INotificationHandler<MonitoringToggledOccurred>>(
    sp => sp.GetRequiredService<ZabbixPollerService>());

// T4.4/T4.5 — BackupCheckEvaluator (pure logic) + BackupMonitorJob (Hangfire,
// NOT Singleton — Scoped, since Hangfire creates a new instance on every run).
builder.Services.AddSingleton<BackupCheckEvaluator>();
builder.Services.AddScoped<BackupMonitorJob>();

// Bug fix (2026-08-23, audit Finding 6.1): AppLogEntries had no retention
// policy — this daily job caps it at 90 days.
builder.Services.AddScoped<AppLogRetentionJob>();

// T4.12 — SLA: on-demand service (Singleton, the same UptimeTrackerService
// instance) + a scheduled Hangfire job.
builder.Services.AddSingleton<SlaReportService>();
builder.Services.AddScoped<SlaReportJob>();

// T5.3 — Telegram: TelegramAccessControlService (Singleton, state cached in
// memory, InitializeAsync called from TelegramBotService.ExecuteAsync) +
// TelegramBotService (a BackgroundService in the SAME process — not a
// separate service, not an HTTP client to our own API; bot commands call
// GetSnapshot()/GetActiveWindows() DIRECTLY on the Phase 4 Singleton
// services).
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

// T4.13 — replacement for FileLoggerService: writes AppLogEntryOccurred to
// the DB. Singleton, NOT Scoped: IMediator, injected into Singleton services
// (UptimeTrackerService etc.), captures the root provider on Publish — no
// handler it resolves can be Scoped. AppLogPersistenceHandler creates its
// own short-lived scope on every call (IServiceScopeFactory).
builder.Services.AddSingleton<INotificationHandler<AppLogEntryOccurred>, AppLogPersistenceHandler>();

var app = builder.Build();

// ── Hangfire recurring jobs (T4.5, T4.12) ────────────────────────────────────
// IRecurringJobManager (the DI-based service API), NOT the static
// RecurringJob — the latter reads JobStorage.Current, which is never
// initialized when Hangfire is configured only via AddHangfire()/DI (rather
// than the legacy GlobalConfiguration.Configuration) — it fails with
// "JobStorage not initialized".
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

    // Bug fix (2026-08-23, audit Finding 6.1).
    recurringJobs.AddOrUpdate<AppLogRetentionJob>(
        "app-log-retention",
        job => job.RunAsync(CancellationToken.None),
        Cron.Daily());
}

// The cron minutes-field step must be 1-59 (Cronos rejects "*/60" as
// invalid, even though functionally it would only ever match minute 0) —
// for intervals ≥60min we switch to an hourly step instead of minutes.
static string EveryNMinutesCron(int minutes)
{
    minutes = Math.Max(minutes, 1);
    if (minutes <= 59) return $"*/{minutes} * * * *";

    int hours = Math.Clamp(minutes / 60, 1, 23);
    return hours == 1 ? "0 * * * *" : $"0 */{hours} * * *";
}

// Local development only: creates/updates the schema on an empty dev DB so
// `dotnet run` works right away without a manual step. The production flow
// still belongs to AdminConsole.Migration (a one-time transfer at cutover,
// Phase 8) — this doesn't replace it, it just removes friction for local
// endpoint testing.
if (app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<AdminConsoleDbContext>().Database.MigrateAsync();
}
else
{
    // Audit Zone 2, Finding #3 (2026-08-22): production deliberately does NOT
    // migrate itself (AdminConsole.Migration.exe is a separate manual
    // deployment step, README → Deployment) — attempting an automatic ALTER
    // TABLE on the live database without the operator's knowledge is also
    // risky. But skipping this step used to surface as an opaque
    // SqliteException ("no such column") somewhere inside whatever
    // background service first touched the new column (that's exactly what
    // happened with RdpDailyPeak). An explicit check here gives a readable
    // message right at startup instead.
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AdminConsoleDbContext>();
    var pending = (await db.Database.GetPendingMigrationsAsync()).ToList();
    if (pending.Count > 0)
    {
        throw new InvalidOperationException(
            $"The database has {pending.Count} pending migration(s): " +
            $"{string.Join(", ", pending)}. Run AdminConsole.Migration.exe " +
            "(next to this .exe) before starting AdminConsole.Api — see README.md → Deployment / Setup.");
    }
}

// With a native Negotiate popup, plain HTTP was tolerable — the
// challenge/response handshake never puts the password on the wire. Now
// that the login form POSTs a real AD password in the request body, anyone
// reaching the app over the plain-HTTP Kestrel endpoint (appsettings.json,
// "Kestrel:Endpoints:Http") would send that password in cleartext, and the
// browser would then silently drop the Secure auth cookie the response
// tries to set — producing the exact "login looks like it failed" symptom
// with no diagnostic. UseHttpsRedirection sends plain-HTTP visitors to the
// HTTPS endpoint before any of that can happen.
app.UseHttpsRedirection();

// UseHsts is skipped in Development on purpose — it would fight the
// self-signed dev certificate workflow (README, "Local Development — HTTPS
// certificate"): a browser that's been told via Strict-Transport-Security
// to only ever use HTTPS for this host would refuse to fall back even when
// the dev cert isn't trusted yet.
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapControllers();
app.MapHub<DashboardHub>("/hubs/dashboard");

// ── T3.10: SPA fallback — MUST be strictly last, or it will intercept /api and /hubs ──
app.MapFallbackToFile("index.html");

app.Run();
