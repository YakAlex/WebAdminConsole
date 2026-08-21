# AdminConsole — Execution Plan для Claude Code

**Призначення документа:** передається безпосередньо в Claude Code як інструкція виконання. Написаний як самодостатній — не покладається на попереднє обговорення поза цим файлом.

**Репозиторій:** `E:\AdminConsole_v2` (поточний WPF-монoліт, джерело для перенесення логіки).
**Ціль:** новий solution поруч (не поверх) — `E:\AdminConsole_v3\` (точну назву узгодь з користувачем перед стартом, якщо не вказано інше — використовуй цю).

---

## 1. Архітектурні рішення (незмінні протягом усього проєкту)

| Рішення | Значення |
|---|---|
| Backend | ASP.NET Core (.NET 8), self-hosted Kestrel, `UseWindowsService()` |
| Service account | **Особистий Domain Admin акаунт користувача** (не gMSA) — див. Реєстр ризиків, п. R1 |
| Auth | Windows Integrated Authentication, `Microsoft.AspNetCore.Authentication.Negotiate` |
| Авторизація | Кастомний `IClaimsTransformation` через `System.DirectoryServices.AccountManagement` (без IIS group-claims не мапляться автоматично) |
| БД | SQLite + EF Core, WAL journal mode |
| Job-планувальник | Hangfire (тільки для job-подібних задач — див. правило нижче), Sqlite storage окремим файлом `hangfire.db` |
| Фонові цикли | `BackgroundService` для тісних циклів (Ping/Zabbix/RDP/EventLog polling) |
| Реал-тайм | SignalR hub `/hubs/dashboard` |
| Внутрішні події | MediatR (`INotification`/`INotificationHandler<T>`) замінює `CommunityToolkit.Mvvm.Messaging.IMessenger` |
| Frontend | React + Vite + SCSS Modules, білд копіюється в `wwwroot`, роздається тим самим Kestrel |
| SPA-роутинг | `app.MapFallbackToFile("index.html")`, зареєстрований ПІСЛЯ `MapControllers()`/`MapHub()` |
| Секрети | ASP.NET Core Data Protection: `.PersistKeysToFileSystem(...)` + `.ProtectKeysWithDpapiNG()` (шифрування ключів at-rest — саме по собі `PersistKeysToFileSystem` зберігає ключі у plaintext XML, тому обов'язково комбінувати з `.ProtectKeysWithDpapiNG()`), плюс NTFS ACL на теку key-ring обмежено тільки на service-акаунт |
| Стратегія переносу | Big Bang, без паралельного WPF+Web |

### Правило Hangfire vs BackgroundService (застосовувати при кожному перенесеному сервісі)
> Hangfire — тільки для дискретних job-подібних задач з інтервалом ≥ хвилини, де корисні retry/dashboard-видимість (`BackupMonitorService`, майбутні SLA-звіти).
> `BackgroundService` — для нескінченних тісних циклів секундного порядку (`PingMonitorService`, `ZabbixPollerService`, `RdpMonitorService`, `RemoteEventLogService`, `ResourceMonitorService`). НЕ переносити ці в Hangfire.

---

## 2. Реєстр ризиків (оновлений)

| # | Ризик | Статус | Дія |
|---|---|---|---|
| R1 | Служба працює під особистим Domain Admin акаунтом користувача, а не під виділеним/gMSA | **Прийнятий ризик, задокументовано за рішенням замовника** | Перед Фазою 7: перевірити GPO `Deny log on as a service` на цей акаунт — якщо заборонено, план блокується на рівні AD, а не коду. Задача R1-check у Фазі 0 |
| R2 | Double-hop / Kerberos delegation | **Закрито** | Сервіс завжди діє від власного імені (service account), не імперсонує browser-юзера — рішення не потребує зміни коду |
| R3 | DPAPI під service-контекстом | **Закрито через дизайн** | `PersistKeysToFileSystem` + `.ProtectKeysWithDpapiNG()` не залежить від того, чи є в акаунта користувацький профіль — обходить питання повністю |
| R4 | AD-групи не мапляться в claims поза IIS | **Прийнято, закладено в план** | Кастомний `IClaimsTransformation`, Фаза 3, тікет 3.4 |
| R5 | Аудит: дії служби невідрізнювані від дій людини в AD Security-логах | **Відкрито, часткова компенсація** | Логон-тип відрізняється на рівні Windows (Service = Logon Type 5, Interactive = Type 2/10) — можна корелювати за типом логону при потребі. Додатково: у Фазі 2 додати `AppLogEntries`/audit-таблицю на рівні застосунку для дій типу `DeleteRecord`/`ClearAllResolved`, щоб мати хоч якийсь app-level audit trail незалежно від AD |

---

## 3. Scaffold — точні команди

```bash
mkdir E:\AdminConsole_v3 && cd E:\AdminConsole_v3

dotnet new sln -n AdminConsole

dotnet new classlib -n AdminConsole.Domain -o AdminConsole.Domain -f net8.0
dotnet new classlib -n AdminConsole.Infrastructure -o AdminConsole.Infrastructure -f net8.0
dotnet new web -n AdminConsole.Api -o AdminConsole.Api -f net8.0
dotnet new console -n AdminConsole.Migration -o AdminConsole.Migration -f net8.0
dotnet new xunit -n AdminConsole.Tests -o AdminConsole.Tests -f net8.0

dotnet sln add AdminConsole.Domain AdminConsole.Infrastructure AdminConsole.Api AdminConsole.Migration AdminConsole.Tests

dotnet add AdminConsole.Infrastructure reference AdminConsole.Domain
dotnet add AdminConsole.Api reference AdminConsole.Domain AdminConsole.Infrastructure
dotnet add AdminConsole.Migration reference AdminConsole.Domain AdminConsole.Infrastructure
dotnet add AdminConsole.Tests reference AdminConsole.Domain AdminConsole.Infrastructure

# NuGet — Infrastructure
dotnet add AdminConsole.Infrastructure package Microsoft.EntityFrameworkCore.Sqlite
dotnet add AdminConsole.Infrastructure package Microsoft.EntityFrameworkCore.Design
dotnet add AdminConsole.Infrastructure package MediatR
dotnet add AdminConsole.Infrastructure package System.DirectoryServices.AccountManagement
dotnet add AdminConsole.Infrastructure package Polly

# NuGet — Api
dotnet add AdminConsole.Api package Microsoft.AspNetCore.Authentication.Negotiate
dotnet add AdminConsole.Api package Hangfire.AspNetCore
dotnet add AdminConsole.Api package Hangfire.Storage.SQLite
dotnet add AdminConsole.Api package Microsoft.AspNetCore.SignalR
dotnet add AdminConsole.Api package MediatR.Extensions.Microsoft.DependencyInjection

# NuGet — Migration
dotnet add AdminConsole.Migration package Microsoft.EntityFrameworkCore.Sqlite

npm create vite@latest adminconsole-web -- --template react
cd adminconsole-web && npm install && npm install @microsoft/signalr sass
```

---

## 4. Тікети по фазах

### Фаза 0 — Підготовка

- **T0.1** — Перевірити GPO `Deny log on as a service` для обраного Domain Admin акаунту на цільовому сервері (R1). Якщо заборонено — ескалувати до користувача негайно, план далі не продовжувати без відповіді.
- **T0.2** — Виконати scaffold-команди з розділу 3.
- **T0.3** — Узгодити з користувачем назву AD-групи для авторизації дашборду (рекомендація: окрема `AdminConsole-Viewers`, не `Domain Admins` — навіть при R1 прийнятому для service-акаунту, авторизація КОРИСТУВАЧІВ дашборду не повинна вимагати Domain Admin прав для перегляду).

### Фаза 1 — Domain layer (перенесення моделей і подій)

- **T1.1** — Скопіювати `E:\AdminConsole_v2\Core\Models\*.cs` → `AdminConsole.Domain\Models\`. Прибрати будь-які WPF-специфічні атрибути/using, якщо є (перевірити кожен файл — очікується, що їх немає, це вже чисті POCO).
- **T1.2** — Створити `AdminConsole.Domain\Events\` з класами-нотифікаціями за таблицею відповідності:

  | Старий (`Core/Messages`) | Новий (`Domain/Events`, `INotification`) |
  |---|---|
  | `AppLogEntryMessage` | `AppLogEntryOccurred(string Source, LogLevel Level, string Message)` |
  | `PingBatchResultMessage` | `PingBatchResultOccurred(PingBatchPayload Payload)` |
  | `MaintenanceChangedMessage` | `MaintenanceChangedOccurred(MaintenanceAction Action, MaintenanceWindow Window)` |
  | `BackupStatusUpdatedMessage` | `BackupStatusUpdatedOccurred(IReadOnlyList<BackupCheckState> Snapshot)` |
  | `BackupTransitionMessage` | `BackupTransitionOccurred(string ServerName, BackupKind Kind, BackupOutcome Previous, BackupOutcome Current)` |
  | `UptimeUpdatedMessage` | `UptimeUpdatedOccurred(IReadOnlyList<DowntimeRecord> Snapshot)` |

- **T1.3** — Створити інтерфейси репозиторіїв у `AdminConsole.Domain\Abstractions\`: `IDowntimeRepository`, `IBackupStateRepository`, `IMaintenanceRepository`, `IAppSettingsRepository`, `IAppLogRepository` (сигнатури методів — дзеркалять поточні `LoadFromDisk`/`SaveToDisk`/`GetSnapshot` у відповідних WPF-сервісах, читай їх напряму з `E:\AdminConsole_v2\Services\` перед написанням інтерфейсу).
- **T1.4 (Definition of Done Фази 1)** — `AdminConsole.Domain` компілюється без залежностей на `CommunityToolkit.Mvvm`, `System.Windows.*`, будь-що WPF.

### Фаза 2 — Персистентність (EF Core + SQLite)

- **T2.1** — `AdminConsoleDbContext` у `AdminConsole.Infrastructure\Data\`. Сутності:
  - `DowntimeRecord` (з `E:\AdminConsole_v2\Core\Models\DowntimeRecord.cs`)
  - `MaintenanceWindow`
  - `BackupCheckState` (1) → `BackupSample` (N), FK `BackupCheckStateId`, включно з полем `LastRawOutcome` (nullable enum)
  - `AppSettings` (single-row: колишній `UserSettings` МІНУС `CloseToTray` — не переносити це поле, воно не має сенсу на сервері)
  - `TelegramAllowedUser` (chat_id, username) — заміна двох паралельних колекцій `TelegramAllowedChatIds`/`TelegramUsernames`
  - `AppLogEntry` (Timestamp, Source, Level, Message) — нова таблиця, заміна файлових логів
  - `MigrationMarker` (одне службове поле — `CompletedAtUtc`)
- **T2.2** — Connection string: `Data Source=adminconsole.db;Cache=Shared`. При відкритті з'єднання виконати `PRAGMA journal_mode=WAL;`.
- **T2.3** — Базовий репозиторій з Polly retry на `SQLITE_BUSY` (3 спроби, 50-200мс backoff) навколо `SaveChangesAsync()`.
- **T2.4** — `dotnet ef migrations add InitialCreate --project AdminConsole.Infrastructure --startup-project AdminConsole.Api`.
- **T2.5** — Реалізувати репозиторії з T1.3, використовуючи EF Core.
- **T2.6** — `AdminConsole.Migration`: консольний застосунок, читає JSON з `E:\AdminConsole_v2\logs\` та `%LocalAppData%\AdminConsole\user_settings.json`, дедуплікує (той самий ключ, що зараз використовує `UptimeTrackerService.LoadFromDisk`/`BackupMonitorService.LoadFromDisk` — перевір ключі в оригінальному коді перед написанням), пише в SQLite, ставить `MigrationMarker`. Ідемпотентний — повторний запуск не дублює дані, лише повідомляє, що вже виконано.
- **T2.7** — `AdminConsole.Tests`: тест на фікстурі (анонімізований зразок `backups.json`/`uptime-*.json`) — прогнати міграцію в тестовій БД, звірити кількість рядків.
- **T2.8 (DoD Фази 2)** — Міграція проганяється на копії реальних даних, звірена вручну кількість записів у кожній таблиці проти вихідних JSON-файлів.

### Фаза 3 — Backend host

- **T3.1** — `Program.cs`: `builder.Host.UseWindowsService()`, Kestrel bind тільки на intranet-інтерфейс (не `0.0.0.0` без потреби).
- **T3.2** — `builder.Services.AddAuthentication(NegotiateDefaults.AuthenticationScheme).AddNegotiate();` + `builder.Services.AddAuthorization(o => o.AddPolicy("Viewer", p => p.RequireRole("<AD-група з T0.3>")));`
- **T3.3** — Data Protection:
  ```csharp
  builder.Services.AddDataProtection()
      .PersistKeysToFileSystem(new DirectoryInfo(@"C:\ProgramData\AdminConsole\keys"))
      .ProtectKeysWithDpapiNG()
      .SetApplicationName("AdminConsole");
  ```
  Після деплою — обмежити NTFS ACL на `C:\ProgramData\AdminConsole\keys` тільки для service-акаунту (icacls, окремий крок деплой-скрипту).
- **T3.4** — `IClaimsTransformation` (R4):
  ```csharp
  public sealed class WindowsGroupClaimsTransformation : IClaimsTransformation
  {
      private readonly string _requiredGroup;

      public WindowsGroupClaimsTransformation(IConfiguration config) =>
          _requiredGroup = config["Authorization:ViewerGroup"]!;

      public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
      {
          if (principal.Identity is not WindowsIdentity identity || !identity.IsAuthenticated)
              return Task.FromResult(principal);

          if (principal.HasClaim(c => c.Type == ClaimTypes.Role && c.Value == _requiredGroup))
              return Task.FromResult(principal); // вже трансформовано (кешується per-request, але захист від подвійного виклику)

          using var context = new PrincipalContext(ContextType.Domain);
          using var user = UserPrincipal.FindByIdentity(context, IdentityType.SamAccountName, identity.Name);

          if (user is not null && user.IsMemberOf(context, IdentityType.SamAccountName, _requiredGroup))
          {
              var clone = principal.Clone();
              ((ClaimsIdentity)clone.Identity!).AddClaim(new Claim(ClaimTypes.Role, _requiredGroup));
              return Task.FromResult(clone);
          }

          return Task.FromResult(principal);
      }
  }
  ```
  Реєстрація: `builder.Services.AddTransient<IClaimsTransformation, WindowsGroupClaimsTransformation>();`. Конфіг `Authorization:ViewerGroup` в `appsettings.json`.
- **T3.5** — Hangfire: `builder.Services.AddHangfire(c => c.UseSQLiteStorage("hangfire.db")); builder.Services.AddHangfireServer();` — окремий файл БД, не змішувати з доменними даними.
- **T3.6** — MediatR: `builder.Services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(SomeDomainMarker).Assembly));`
- **T3.7** — `DashboardHub : Hub` у `AdminConsole.Api\Hubs\`, групи: `ping`, `uptime`, `backups`, `logs`. Клієнт підписується на групу при вході на відповідну сторінку React.
- **T3.8** — `SignalRBroadcastHandler` — `INotificationHandler<T>` для кожної події з T1.2, викликає `IHubContext<DashboardHub>.Clients.Group(...).SendAsync(...)`.
- **T3.9** — REST-контролери: `GET /api/servers`, `GET /api/downtime`, `GET /api/backups`, `GET /api/logs?take=1000&before={timestamp}`.
- **T3.10 — SPA fallback (за запитом користувача):**
  ```csharp
  app.UseStaticFiles();

  app.MapControllers();
  app.MapHub<DashboardHub>("/hubs/dashboard");

  app.MapFallbackToFile("index.html"); // ОБОВ'ЯЗКОВО останнім — щоб не перехоплював /api та /hubs
  ```
- **T3.11 (DoD Фази 3)** — Локальний запуск `dotnet run` в `AdminConsole.Api`: `GET /api/servers` повертає 401 без Windows-автентифікації, 200 з нею (для юзера з потрібної AD-групи), 403 для юзера без групи.

### Фаза 4 — Перенесення моніторингових сервісів

Для кожного сервісу з таблиці нижче: прочитати оригінал з `E:\AdminConsole_v2\Services\<File>.cs`, перенести логіку 1:1 (лише заміна `IMessenger.Send` на `IMediator.Publish`, заміна `LoadFromDisk`/`SaveToDisk` на виклики репозиторію з Фази 2), зберігаючи ВСІ існуючі локи/`ConcurrentDictionary`/коментарі щодо thread-safety без змін.

| Тікет | Сервіс | Ціль | Примітка |
|---|---|---|---|
| T4.1 | `MaintenanceService.cs` | `BackgroundService`, `AddSingleton` | Мігрувати ПЕРШИМ — залежність для T4.2-T4.4 |
| T4.2 | `PingMonitorService.cs` | `BackgroundService`, `AddSingleton` | Без Hangfire — тісний цикл |
| T4.3 | `UptimeTrackerService.cs` | `BackgroundService` + `INotificationHandler<PingBatchResultOccurred>`, `AddSingleton` | |
| T4.4 | `BackupCheckEvaluator.cs` | Переноситься майже без змін в `Infrastructure/Monitoring` | Залежність `WinEventLogReader.IsReachableAsync` — переноситься разом |
| T4.5 | `BackupMonitorService.cs` | **Hangfire recurring job**, `[BackgroundJob]`/`RecurringJob.AddOrUpdate` кожні `BackupPollIntervalMinutes` | Уся анти-флапінг/lock-логіка з `_stateLock` переноситься без змін |
| T4.6 | `EventLogReader.cs` (`WinEventLogReader`) | Переноситься без змін в `Infrastructure/Remote` | |
| T4.7 | `EventLogService.cs`, `RemoteEventLogService.cs` | `BackgroundService` | |
| T4.8 | `RdpMonitorService.cs`, `RdpCredentialValidator.cs` | `BackgroundService` | |
| T4.9 | `RemoteManagementService.cs`, `RemoteResourceService.cs`, `ResourceMonitorService.cs` | `BackgroundService` | |
| T4.10 | `ZabbixApiClient.cs` | Переноситься без змін | |
| T4.11 | `ZabbixPollerService.cs` | `BackgroundService` | |
| T4.12 | `SlaReportService.cs`, `SlaReportHtmlRenderer.cs` | Hangfire job (за розкладом) + API-ендпоінт (on-demand) | |
| T4.13 | `FileLoggerService.cs` | **Видалити**, замінити на запис в `AppLogEntries` через `IAppLogRepository` | Прибирає весь клас проблем із multi-file merge |
| T4.14 | `CredentialPromptCoordinator.cs`, `ICredentialPrompt.cs`, `IDialogService.cs`, `OverlayDialogService.cs` | **Не переносити, видалити** | Замінюється React Settings-сторінкою (Фаза 5) |

- **T4.15 (DoD Фази 4)** — Кожен сервіс має юніт-тест у `AdminConsole.Tests` хоча б на одну критичну гілку логіки (анти-флапінг для Backup, reconciliation для Uptime) — раніше цього не було можливо через прив'язку до WPF.

### Фаза 5 — Секрети та Telegram

- **T5.1** — `CredentialStore.cs`: переписати backing store на Data Protection API (T3.3), лишити публічний контракт (метод-сигнатури) без змін, де можливо.
- **T5.2** — React-сторінка `Settings/Credentials`: форма введення RDP/Zabbix credentials, `POST /api/credentials`, авторизація — та сама `Viewer`-політика (або окрема `Admin`-політика, якщо потрібне розділення прав перегляду vs зміни — уточнити з користувачем).
- **T5.3** — `TelegramBotService.cs`, `TelegramAccessControlService.cs` → `IHostedService`, реєстрація в `Program.cs`, DI напряму на singleton-сервіси з Фази 4 (без HTTP-прошарку).

### Фаза 6 — Frontend

- **T6.1** — Сторінки: `Dashboard`, `Uptime`, `Backups`, `Logs`, `Maintenance`, `Settings`.
- **T6.2** — SignalR-клієнт: `new HubConnectionBuilder().withUrl("/hubs/dashboard", { withCredentials: true }).build()`.
- **T6.3** — Дизайн-система — окреме обговорення, не блокує решту плану.

### Фаза 7 — Розгортання

- **T7.1** — `dotnet publish -c Release -r win-x64 --self-contained true` **БЕЗ** `-p:PublishSingleFile=true` (Kestrel-хост без WPF/BAML не має проблеми temp-extraction, яку мав WPF-клієнт — навмисно не вмикати цю опцію).
- **T7.2** — Build-скрипт: `npm run build` в `adminconsole-web`, копіювання `dist/*` → `AdminConsole.Api\wwwroot\`.
- **T7.3** — Встановлення служби (враховуючи R1 — Domain Admin, не gMSA):
  ```powershell
  New-Service -Name "AdminConsoleService" `
    -BinaryPathName "C:\Deploy\AdminConsole\AdminConsole.Api.exe" `
    -Credential (Get-Credential) `   # інтерактивно введе Domain Admin пароль
    -StartupType Automatic
  ```
  Або через `sc.exe`, якщо потрібен non-interactive скрипт (пароль передається окремо, НЕ хардкодиться в скрипті — запит через захищений prompt чи передача через Credential Manager на цільовій машині).
- **T7.4** — `icacls` на теку Data Protection key-ring (T3.3) — обмежити тільки на service-акаунт.
- **T7.5** — Перший запуск під наглядом: перевірити T0.1 (Deny log on as a service), перевірити, що WMI/quser/SMB-виклики реально проходять під цим акаунтом на всіх 10 цільових серверах.

### Фаза 8 — Cutover

- **T8.1** — Запустити `AdminConsole.Migration` на реальних продакшн-даних.
- **T8.2** — Контрольний період звірки цифр (WPF vs новий дашборд) — навіть при Big Bang.
- **T8.3** — Відключити WPF, зберегти JSON-файли як holdback (не видаляти).

### Фаза 9 — Hardening

- **T9.1** — Перевірити 401/403 сценарії авторизації.
- **T9.2** — Базове навантажувальне тестування (3 одночасних SignalR-клієнти).
- **T9.3** — `ARCHITECTURE.md` у репозиторії — перенести розділи 1-2 цього документа плюс фактичні рішення, прийняті під час імплементації.

---

## 5. Порядок виконання для Claude Code

Виконувати фази строго послідовно (0 → 9), тікети всередині фази — у вказаному порядку там, де є явні залежності (позначено в примітках, напр. T4.1 перед T4.2-T4.4). Після кожної фази — зупинитись, дати користувачу перевірити DoD, чекати підтвердження перед стартом наступної фази.
