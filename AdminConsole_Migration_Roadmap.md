# AdminConsole v2 → v3: Roadmap міграції WPF → ASP.NET Core + React

**Статус:** узгоджено, готово до старту Фази 0
**Формат роботи:** повний план для тебе (замовник/тестувальник/DevOps) і для мене (виконавець). Я маю лише read-only доступ до `E:\AdminConsole_v2` — весь новий код і всі diff'и (BULO/СТАЛО) я даю текстом у чат, ти застосовуєш, білдиш, тестуєш і повідомляєш результат. Це буде ітеративний процес по кожній фазі: я даю код → ти застосовуєш → компіляція/тест → наступний крок.

---

## 0. Прийняті рішення (для швидкої звірки)

| Питання | Рішення |
|---|---|
| Service account | Виділений gMSA, права на Event Log / WMI / quser / SMB на цільових серверах |
| Auth фронтенду | Windows Integrated Authentication (Negotiate/Kerberos), авторизація по AD-групі |
| Персистентність | SQLite + EF Core, WAL-режим |
| Стратегія міграції | Big Bang (без паралельного співіснування WPF і Web) |
| Telegram-бот | `IHostedService` всередині того самого ASP.NET Core процесу |
| Секрети | Data Protection API з persisted key ring під gMSA, або Windows Credential Manager під gMSA |
| Фонова робота | Hangfire для job-подібних задач, `BackgroundService` для тісних циклів (обґрунтування — Фаза 1) |
| Хостинг | Kestrel self-hosted всередині Windows Service, без IIS. Роздає API + SignalR + React-статику одним процесом |
| Обмін подіями | MediatR (обґрунтування нижче) |
| Масштаб | ~10 серверів, 1-3 одночасних адміни, 1 розробник |

---

## Цільова структура рішення (solution)

```
AdminConsole.sln
├── AdminConsole.Domain/          # POCO-моделі, enum'и, чиста бізнес-логіка. Без WPF, без ASP.NET Core, без EF Core.
│   ├── Models/                   # ← Core/Models з поточного проєкту (майже 1:1)
│   ├── Events/                   # ← Core/Messages, перейменовані під MediatR INotification
│   └── Abstractions/             # інтерфейси на кшталт IBackupStateRepository, IDowntimeRepository
│
├── AdminConsole.Infrastructure/  # Реалізації: EF Core DbContext, зовнішні адаптери
│   ├── Data/                     # AdminConsoleDbContext, EF Migrations, репозиторії
│   ├── Monitoring/               # PingMonitorService, UptimeTrackerService, BackupMonitorService, ...
│   ├── Remote/                   # EventLogReader, RemoteEventLogService, RdpMonitorService, RemoteResourceService
│   ├── Zabbix/                   # ZabbixApiClient, ZabbixPollerService
│   ├── Telegram/                 # TelegramBotService, TelegramAccessControlService
│   └── Security/                 # CredentialStore (переписаний під gMSA/DPAPI-service-context)
│
├── AdminConsole.Api/             # ASP.NET Core host = Windows Service
│   ├── Program.cs                # UseWindowsService(), Kestrel, Negotiate, Hangfire, SignalR, SPA fallback
│   ├── Hubs/                     # DashboardHub (SignalR)
│   ├── Controllers/               # REST API (початкове завантаження даних)
│   ├── Jobs/                     # Hangfire recurring jobs (BackupCheck, SLA-звіти)
│   └── wwwroot/                  # сюди при publish копіюється React-білд
│
├── AdminConsole.Migration/       # одноразова консольна утиліта: JSON-файли → SQLite (Фаза 2)
│
├── AdminConsole.Tests/           # xUnit — юніт-тести для Domain/Infrastructure (раніше цього не було, бо WPF важко тестувати)
│
└── adminconsole-web/             # React + Vite + SCSS Modules SPA, окремий npm-проєкт
```

**Чому саме такий розподіл:** `Domain` не залежить ні від чого — це дозволяє його юніт-тестити без піднятого хоста/БД. `Infrastructure` знає про `Domain`, але не про `Api`. `Api` — тонкий шар: контролери/хаби/джоби викликають сервіси з `Infrastructure` через DI, самі майже без логіки. Це стандартна Clean/Onion-структура, яка добре лягає на вже наявний у тебе стиль "маленькі сфокусовані сервіси".

---

## Фаза 0 — Підготовка (паралельно з рештою, не блокує старт кодування)

- [ ] Заявка в AD на gMSA-акаунт + права: Event Log (remote), WMI/DCOM для `quser`-еквіваленту, read на SMB-шари з бекапами. **Це найдовший за часом пункт (узгодження з AD-адмінами) — подавай заявку сьогодні, а не коли дійдемо до Фази 3.**
- [ ] Створити порожній solution зі структурою вище (`dotnet new` команди дам у Фазі 1, коли підеш застосовувати).
- [ ] Зафіксувати в AD, яка саме група буде авторизаційною межею для дашборду (`Domain Admins` чи окрема `AdminConsole-Viewers`/`AdminConsole-Admins` — рекомендую **окрему групу**, а не `Domain Admins`: прив'язка авторизації вебпанелі моніторингу до найвищих прав домену — зайвий ризик і незручність, якщо колись знадобиться дати доступ комусь без домен-адмінських прав).

---

## Фаза 1 — Відокремлення бізнес-логіки (найдетальніше, як просив)

### 1.1 Класифікація існуючих сервісів

Я вже читав увесь `Services/`-каталог у попередніх сесіях — ось конкретний план по кожному файлу:

| Файл | Що з ним робимо | Коментар |
|---|---|---|
| `BackupCheckEvaluator.cs` | Переносимо **майже без змін** у `Domain` (чиста логіка, вже stateless) | Єдина залежність — `WinEventLogReader.IsReachableAsync` (наш нещодавній фікс), лишається в `Infrastructure/Remote` |
| `BackupMonitorService.cs` | → **Hangfire recurring job** | Інтервал 60 хв — ідеальний кандидат: дискретний "job run", а не тісний цикл. Hangfire дає retry й видимість у dashboard безкоштовно |
| `PingMonitorService.cs` | → лишається `BackgroundService` | Цикл 10-30с — це занадто тісно для Hangfire (мінімальна практична гранулярність cron — хвилини). Переносити не варто, тільки шкодить |
| `UptimeTrackerService.cs` | → `BackgroundService` (реагує на повідомлення, не на таймер) | Підписник на `PingBatchResultMessage` — стає `INotificationHandler<PingBatchResultOccurred>` |
| `MaintenanceService.cs` | → `BackgroundService`/сервіс-синглтон | Спільна залежність для Ping/Uptime/Backup — мігрувати **першим** серед stateful-сервісів |
| `EventLogReader.cs` (`WinEventLogReader`) | Переносимо без змін у `Infrastructure/Remote` | Статичний клас, нуль залежностей від WPF |
| `EventLogService.cs`, `RemoteEventLogService.cs` | → `BackgroundService` | ⚠️ див. ризик "Kerberos double-hop" нижче |
| `RdpMonitorService.cs`, `RdpCredentialValidator.cs` | → `BackgroundService` | ⚠️ той самий double-hop ризик — `quser` на віддаленому сервері з-під gMSA |
| `RemoteManagementService.cs`, `RemoteResourceService.cs`, `ResourceMonitorService.cs` | → `BackgroundService` | Той самий патерн |
| `ZabbixApiClient.cs` | Переносимо без змін | Звичайний HTTP-клієнт, нуль проблем |
| `ZabbixPollerService.cs` | → `BackgroundService` | Цикл 60с — тісний, лишається як є |
| `SlaReportService.cs`, `SlaReportHtmlRenderer.cs` | → Hangfire job (за розкладом) **+** API-ендпоінт (на вимогу) | React показує JSON, а HTML-рендер лишається для "завантажити звіт" |
| `TelegramBotService.cs`, `TelegramAccessControlService.cs` | → `IHostedService` у тому самому процесі | Як домовились (Фаза 4) |
| `UserSettingsService.cs` | → перезаписується поверх EF Core (Фаза 2) | `CloseToTray` — **видаляємо** (немає сенсу на сервері). `RdpMonitoringEnabled`/`ZabbixMonitoringEnabled` — стають фіче-тогглами в БД з адмін-UI |
| `CredentialStore.cs` | Переписуємо backing store (Фаза 5) | Логіка "що зберігаємо" лишається, "як зберігаємо" — змінюється |
| `CredentialPromptCoordinator.cs`, `ICredentialPrompt.cs`, `IDialogService.cs`, `OverlayDialogService.cs` | **Не мігруються, видаляються** | Це WPF-модальні діалоги "введи пароль зараз" — у headless Windows Service немає користувача, якого можна спитати. Замінюється Settings-сторінкою в React (детальніше — Фаза 5) |
| `FileLoggerService.cs` | Опційно замінюється на Serilog | Не обов'язково, але дає структуровані логи "з коробки" разом з Hangfire/SignalR/HTTP-логами в тому самому пайплайні. Або можна винести `AppLogEntry` у БД — і разом з цим зникає вся нещодавно написана логіка "кілька файлів логів" (`FindRecentLogFiles`/`ParseTailLines`) — просто `ORDER BY Timestamp DESC LIMIT 1000` в SQL |

### 1.2 Заміна `IMessenger` на MediatR — чому саме MediatR

Розглядав три варіанти:

- **`CommunityToolkit.Mvvm.Messaging` (лишити як є)** — технічно він не WPF-специфічний і працює поза UI. Але `WeakReferenceMessenger` спроєктований під сценарій "UI-елемент підписався, зник з екрана, GC сам відписав" — для довгоживучих singleton-сервісів ASP.NET Core ця "фіча" не потрібна і додає плутанини без користі.
- **`System.Threading.Channels`** — найбільший контроль і продуктивність, але це "збери свій MediatR сам": свій dispatcher, свій реєстр підписників, своя обробка помилок у хендлерах. При масштабі "10 серверів, 1 розробник" — це чистий over-engineering.
- **MediatR (обраний варіант)** — один NuGet-пакет, `services.AddMediatR(...)` сканує збірку і сам реєструє `INotificationHandler<T>`, з коробки async fan-out (усі хендлери виконуються паралельно, throw з одного не гасить інші — саме та поведінка, яка вже неявно є у твоєму коді через `catch (Exception ex)` навколо кожного виклику). Ідіоматично для ASP.NET Core, легко додати pipeline behavior (наприклад, логування кожної події) одним класом.

**Мапінг Core/Messages → MediatR** (механічна заміна, зроблю списком diff'ів, коли дійдемо до коду):

| Було (`IMessenger.Send`) | Стає (`IMediator.Publish`) |
|---|---|
| `AppLogEntryMessage.Info/Warning/Error/Success(...)` | `AppLogEntryOccurred` (`INotification`) |
| `PingBatchResultMessage` | `PingBatchResultOccurred` |
| `MaintenanceChangedMessage` | `MaintenanceChangedOccurred` |
| `BackupStatusUpdatedMessage` | `BackupStatusUpdatedOccurred` |
| `BackupTransitionMessage` | `BackupTransitionOccurred` |
| `UptimeUpdatedMessage` | `UptimeUpdatedOccurred` |

Кожен `IRecipient<T>` (наприклад, `UptimeTrackerService : IRecipient<PingBatchResultMessage>`) стає окремим класом `INotificationHandler<PingBatchResultOccurred>`, зареєстрованим у DI. А **новий** хендлер `SignalRBroadcastHandler : INotificationHandler<UptimeUpdatedOccurred>, INotificationHandler<BackupStatusUpdatedOccurred>, ...` — це і є міст до вебдашборду: замість того щоб UI напряму підписувався через `IMessenger`, кожна доменна подія автоматично летить у `IHubContext<DashboardHub>.Clients.All.SendAsync(...)`.

### 1.3 Hangfire vs BackgroundService — фінальне правило

> **Hangfire** — коли задача дискретна, має чіткий "запуск/завершення", виграє від retry-політики й видимості в dashboard, і інтервал вимірюється хвилинами+. Зараз це тільки `BackupMonitorService` і майбутні заплановані SLA-звіти.
> **`BackgroundService`** — коли це тісний нескінченний цикл (секунди), де сама "довгоживучість" процесу і є частиною дизайну (напр. `PingMonitorService` з двома паралельними циклами main+recovery). Переносити такий код у Hangfire — не виграш, а штучне ускладнення.

### 1.4 Concurrency-аудит (важливо саме через ЗБІЛЬШЕННЯ кількості одночасних споживачів)

Кожен stateful-сервіс (`PingMonitorService`, `UptimeTrackerService`, `BackupMonitorService`, `MaintenanceService`, `ResourceMonitorService`, ...) реєструється в DI як **`AddSingleton`**, не `AddScoped` — стан має жити на весь процес і бути спільним. Існуючі локи (`_lock`, `_saveLock`, `_stateLock`, `ConcurrentDictionary`) лишаються — вони вже написані правильно під кілька одночасних читачів/писачів (це саме те, що ми зміцнили в `BackupMonitorService` минулої сесії). Тепер просто зростає кількість реальних одночасних клієнтів: замість одного WPF UI-потоку — кілька браузерних SignalR-з'єднань + HTTP-запити + Hangfire-джоба одночасно. Існуючий патерн це вже витримує, змінювати підхід не треба.

### 1.5 ⚠️ Ризик, який треба перевірити рано: Kerberos double-hop

gMSA-акаунт, під яким крутиться Windows Service, робитиме запити до **третіх** машин (Event Log на сервері X, `quser`/WMI на сервері Y, SMB-шара на сервері Z) від імені користувача, який автентифікувався в браузері через Negotiate. Якщо десь у коді неявно очікується "передати" ідентичність кінцевого користувача далі (delegation) — це класична **double-hop problem**: NTLM її взагалі не підтримує, Kerberos підтримує лише за явно налаштованої **constrained delegation** в AD.

**У нашому випадку це, найімовірніше, НЕ проблема** — бо `EventLogReader`/`RdpMonitorService`/`BackupCheckEvaluator` і так завжди виконували запити від імені **процесу** (раніше — інтерактивного WPF-юзера, тепер — gMSA), а не від імені "того, хто дивиться в дашборд". Тобто delegation не потрібен: gMSA має власні права на цільові сервери, і саме gMSA (не браузер-юзер) робить усі віддалені виклики. Але це треба **явно підтвердити на першому тестовому деплої** (Фаза 3-4), перш ніж вважати само собою зрозумілим — якщо в WMI/RDP-виклику десь є `WindowsIdentity.Impersonate()` чи щось подібне, там доведеться копати глибше.

### 1.6 ⚠️ Ризик: авторизація по AD-групі поза IIS

На IIS `WindowsPrincipal` автоматично мав ролі = AD-групи "з коробки". У Kestrel + `Microsoft.AspNetCore.Authentication.Negotiate` (офіційний пакет, підтримує self-host без IIS — це те, що нам і треба) групова приналежність **не мапиться в claims автоматично**. Треба буде явно перевіряти членство в групі через `System.DirectoryServices.AccountManagement` (`GroupPrincipal`/`UserPrincipal.GetAuthorizationGroups()`) в кастомному `IClaimsTransformation` або authorization policy handler. Закладаю це окремим підпунктом у Фазу 3, не то на проді авторизація або не спрацює, або "пропустить усіх".

---

## Фаза 2 — Персистентність і міграція даних (найдетальніше, як просив)

### 2.1 EF Core сутності (мапінг з поточних `Core/Models`)

| Поточна модель (файл, JSON) | Нова EF Core сутність / таблиця | Примітка |
|---|---|---|
| `DowntimeRecord` (`logs/uptime-*.json`) | `DowntimeRecords` | Пряме перенесення полів |
| `MaintenanceWindow` | `MaintenanceWindows` | Пряме перенесення |
| `BackupCheckState` + `BackupSample` (`logs/backups.json`) | `BackupCheckStates` (1) → `BackupSamples` (N), FK по `BackupCheckStateId` | Зараз `History` — вкладений `List<BackupSample>` у JSON; в реляційній моделі стає дочірньою таблицею. Не забути `LastRawOutcome` (поле, яке ми щойно додали) |
| `UserSettings` (`%LocalAppData%\...\user_settings.json`) | `AppSettings` (single-row-таблиця) + `TelegramAllowedUsers` (child) | `CloseToTray` — **прибирається**, `TelegramUsernames`/`TelegramAllowedChatIds` → окрема таблиця замість двох паралельних колекцій |
| — (новe) | `AppLogEntries` | Заміна файлових `app-*.log`. Вирішує вже наявний баг з multi-file merge одним `ORDER BY Timestamp DESC LIMIT :n` |
| — (новe) | `__MigrationMarker` | Службова таблиця-прапорець "одноразова міграція з JSON вже виконана" |

### 2.2 `AdminConsoleDbContext` і WAL

```
Data Source=adminconsole.db;Cache=Shared
```
+ виконати одразу після відкриття першого з'єднання: `PRAGMA journal_mode=WAL;` (в SQLite це persistent-налаштування файлу БД, досить виконати один раз при ініціалізації, але безпечніше ставити при кожному `OnConfiguring`/`DbConnection.Open` — дешева ідемпотентна операція).

**Важливий нюанс WAL:** дозволяє **один writer + багато читачів** одночасно без блокувань — саме наш кейс (Hangfire-джоба пише, кілька SignalR-клієнтів через API читають). Але кілька писачів одночасно (наприклад, дві Hangfire-джоби записують у той самий момент) все одно серіалізуються на рівні SQLite і можуть кинути `SQLITE_BUSY`. EF Core SQLite-провайдер **не ретраїть** такі помилки сам — додамо тонкий Polly-retry (3 спроби, invervals ~50-200мс) навколо `SaveChangesAsync()` в базовому репозиторії. При нашому масштабі (10 серверів, кілька admin-запитів) це радше теоретичний захист, ніж реальна необхідність, але коштує 10 рядків коду і повністю знімає питання.

### 2.3 Стратегія "репозиторій замість прямого File I/O", а не переписування бізнес-логіки

Ключове рішення, яке мінімізує ризик регресій: **логіка анти-флапінгу, reconciliation при старті, дедуплікація downtime-записів і т.д. лишається такою, якою є**. Змінюється тільки нижній шар:

- `LoadFromDisk()` → `LoadFromDbAsync()` (наповнює ті самі in-memory `List`/`Dictionary`, які й зараз кешують дані для швидкого доступу з hot-path — наприклад, перевірка Maintenance Window при кожному пінгу не повинна бити в БД щоразу).
- `SaveToDisk()` → `SaveToDbAsync()` (той самий "записати snapshot", просто через `DbContext` замість `File.WriteAllText` + `File.Move`).
- Уся логіка **навколо** цих двох методів (лічильники, анти-флапінг, reconciliation, дедуплікація) — **не чіпається**.

Це свідомий вибір "мінімальний diff, максимальна перевіреність" замість переписування сервісів з нуля під ORM-first підхід.

### 2.4 Одноразовий інструмент міграції (`AdminConsole.Migration`)

Консольний застосунок, що виконується **один раз** під час cutover (Фаза 8):

1. Читає існуючі `logs/uptime-*.json`, `logs/backups.json`, `logs/maintenance.json`, `%LocalAppData%\AdminConsole\user_settings.json` зі старого шляху інсталяції — тими самими POCO-класами, що вже є (структура полів не змінюється, тільки спосіб зберігання).
2. Дедуплікує так само, як зараз робить `UptimeTrackerService.LoadFromDisk()` (`ServerIp` + `FellAt`) і `BackupMonitorService.LoadFromDisk()` (по ключу `ServerName|Kind`) — щоб уникнути дублів, якщо міграцію випадково запустять двічі.
3. Записує все через EF Core в SQLite.
4. Ставить прапорець у `__MigrationMarker` — **ідемпотентність**: повторний запуск нічого не зробить і явно про це повідомить, а не мовчки задублює дані.
5. **Юніт-тест на фікстурі**: беремо реальний (анонімізований) `backups.json`/`uptime-2026-08.json` як тестові дані, ганяємо міграцію в тестовій БД, звіряємо кількість рядків і кілька контрольних значень. Це одноразова, неповторювана операція над продакшн-даними — вартість написання тесту (~30 хв) значно нижча за вартість втраченої історії даунтайму/бекапів через банальний баг парсингу.

### 2.5 Порядок дій у Фазі 2 (чекліст)

- [ ] `dotnet ef migrations add InitialCreate` на основі сутностей з 2.1
- [ ] Репозиторії: `IDowntimeRepository`, `IBackupStateRepository`, `IMaintenanceRepository`, `IAppSettingsRepository`, `IAppLogRepository` — інтерфейси в `Domain/Abstractions`, реалізації в `Infrastructure/Data`
- [ ] Переписати `LoadFromDisk`/`SaveToDisk` у кожному мігрованому сервісі на виклики репозиторію (детальний diff дам по кожному файлу окремо, коли дійдемо)
- [ ] Написати `AdminConsole.Migration` + юніт-тест на фікстурі
- [ ] **Не видаляти старі JSON-файли** після міграції — залишити як holdback до підтвердженого стабільного тижня роботи нової системи

---

## Фаза 3 — Backend host

- [ ] `Program.cs`: `builder.Host.UseWindowsService()`, Kestrel-конфігурація (порт, тільки intranet-інтерфейс)
- [ ] `Microsoft.AspNetCore.Authentication.Negotiate` — SSO без IIS
- [ ] Кастомна `IClaimsTransformation` для мапінгу AD-групи в role-claim (ризик 1.6)
- [ ] `AddHangfire()` + `UseSqLiteStorage` (Hangfire теж вміє в SQLite-сховище для job-даних — окрема БД чи та сама, вирішимо, коли дійдемо; схиляюсь до окремого файлу `hangfire.db`, щоб не змішувати доменні дані з службовими job-логами Hangfire)
- [ ] `DashboardHub : Hub` — групи по типу даних (`ping`, `uptime`, `backups`, `logs`) щоб клієнт підписувався тільки на те, що показує поточна сторінка
- [ ] REST-контролери для початкового завантаження (`GET /api/servers`, `GET /api/downtime`, `GET /api/backups`, `GET /api/logs?take=1000` — саме той запит, що замінює всю multi-file логіку)
- [ ] Authorization policy: `RequireRole("AdminConsole-Viewers")` за замовчуванням на все API/Hub

## Фаза 4 — Telegram-бот

- [ ] `TelegramBotService`/`TelegramAccessControlService` → `IHostedService`, реєструються в тому самому `Program.cs`
- [ ] DI-залежності напряму на ті самі singleton-сервіси (`PingMonitorService.GetSnapshot()`, `BackupMonitorService.GetSnapshot()`) — без HTTP-прошарку, прямі виклики в межах процесу

## Фаза 5 — Секрети

- [ ] `CredentialStore` переписується під `IDataProtectionProvider` (ASP.NET Core Data Protection) з `PersistKeysToFileSystem` у папку, доступну gMSA, або `.ProtectKeysWithDpapiNG` для сумісності з обома підходами — деталі оберемо після підтвердження, що DPAPI під service-контекстом (без залогіненого user-профілю) взагалі коректно ініціалізується на цільовому сервері (це теж варто перевірити рано, аналогічно до 1.5)
- [ ] Нова React-сторінка "Налаштування → Облікові дані" замість WPF-модалок (`CredentialPromptCoordinator` і UI-компаньйони видаляються, як зазначено в 1.1)

## Фаза 6 — Frontend (React + Vite + SCSS Modules)

- [ ] Каркас: `Dashboard` (ping-статуси), `Uptime/SLA`, `Backups`, `Logs`, `Maintenance`, `Settings`
- [ ] SignalR-клієнт (`@microsoft/signalr`), `withCredentials: true` для прозорого SSO через Negotiate
- [ ] Дизайн-система винесемо в окрему розмову, коли дійдемо — це вже не архітектура, а UI/UX-рішення

## Фаза 7 — Розгортання

- [ ] `dotnet publish` (self-contained, **без** `PublishSingleFile` — Kestrel-хост не має WPF/BAML-залежностей, тому весь той клас проблем із single-file temp-extraction, який ми фіксили в WPF-версії, тут **не виникає в принципі**, якщо просто не вмикати цю опцію)
- [ ] React-білд (`vite build`) копіюється у `AdminConsole.Api/wwwroot` перед публікацією (окремий build-крок/скрипт)
- [ ] `sc create AdminConsoleService binPath= "...\AdminConsole.Api.exe" obj= "DOMAIN\gMSA$" start= auto`
- [ ] xcopy-інсталяція на цільовий сервер, перший запуск під наглядом (перевірка double-hop і DPAPI-ризиків з 1.5/5)

## Фаза 8 — Cutover

- [ ] Запуск `AdminConsole.Migration` на продакшн-даних
- [ ] Паралельна перевірка "новий дашборд показує ті самі цифри, що й старий WPF" протягом контрольного періоду (навіть у Big Bang — кілька днів звірки перед повним відключенням WPF)
- [ ] Відключення WPF-клієнта, holdback JSON-файлів (не видаляти одразу)

## Фаза 9 — Тестування / hardening

- [ ] Перевірка авторизації: юзер не з дозволеної групи → 403, юзер з групи → доступ
- [ ] Базове навантажувальне "перевір, що нічого не падає" на 3 одночасні SignalR-з'єднання (тривіально при такому масштабі, але це 20 хвилин роботи, які знімають питання)
- [ ] README/нотатки з архітектурних рішень цього документа — переносяться в репозиторій як `ARCHITECTURE.md`, щоб через півроку сам не загубився в контексті

---

## Відкриті ризики, які варто закрити РАНО (не наприкінці)

1. **Double-hop / delegation** (1.5) — перевірити на першому ж тестовому деплої Фази 3-4, до того як зав'язувати на цьому весь план RDP/EventLog-моніторингу.
2. **AD-групи в claims поза IIS** (1.6) — без цього авторизація або пропустить усіх, або не пустить нікого.
3. **DPAPI під service-контекстом gMSA** (Фаза 5) — перевірити мінімальним PoC-скриптом раніше, ніж переписувати весь `CredentialStore`.

---

## Наступний крок

Готовий почати з **Фази 0 + Фази 1.1** — тобто конкретні `dotnet new`-команди для solution-структури і перший реальний diff: винесення `Core/Models`/`Core/Messages` у `AdminConsole.Domain` без змін логіки. Скажи, коли готовий стартувати — почнемо покроково, по одному сервісу за раз, з тестуванням на кожному кроці.
