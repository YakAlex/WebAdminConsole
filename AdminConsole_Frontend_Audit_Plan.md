# Frontend Audit — план усунення (2026-08-22)

Джерело: повний статичний аудит `adminconsole-web` (5 категорій: мертві кнопки,
мок-дані, обірвані API-звʼязки, відсутні loading/error, орфанні файли) +
рішення користувача щодо пріоритетів і двох втрачених WPF-фіч (дії на Ping,
SLA-звіт).

---

## Пріоритет 1 — Сліпі зони ✅ ЗАВЕРШЕНО (2026-08-22)

### 1.1 REST-снепшот для Zabbix Alerts

- Зараз `useZabbixProblems()` — чисто SignalR, без початкового REST-запиту;
  `GET /api/zabbix*` не існує на бекенді взагалі.
- Новий `ZabbixController.Get()` → `GET /api/zabbix`, що робить **живий
  опит зараз** (мова про реальний виклик Zabbix API в момент запиту, не
  кеш) — новий публічний метод `ZabbixPollerService.GetActiveProblemsNowAsync`,
  той самий принцип, що вже є в `PingController`/`PingMonitorService.PingAllNowAsync`.
- `useZabbixProblems` переписується під формат `{ payload, loading, error }`
  (REST init + SignalR live) — той самий патерн, що вже в `usePingStream`.
  Оновити виклики: `useZabbixAlertsPageViewModel`, `useDashboardData`.

### 1.2 REST-снепшот для RDP Sessions

- Той самий брак: `useRdpSessions()` — SignalR-only, нуль REST.
- Новий `RdpSessionsController.Get()` → `GET /api/rdp-sessions`, новий
  публічний метод `RdpMonitorService.GetSnapshotNowAsync` — реально
  опитує термінальні сервери (quser) НАРАЗІ, повертає агрегат
  (сесії + globalDailyPeak + lastLogout), і заразом штовхає ті самі
  SignalR-події, що й фоновий цикл (клієнт, що ініціював запит, побачить
  дані і через REST-відповідь, і через SignalR).
- `useRdpSessions` переписується: REST-знімок як початковий seed, живі
  SignalR-події per-server — як і раніше, з переходом на live-модель
  щойно прийде перша подія.

### 1.3 Loading / Error UI

- Нові переюзабельні `components/ui/Spinner` та `components/ui/ErrorBanner`
  (зараз інлайн-банер помилки дублюється в Logs/Settings/Uptime — виносимо
  в один компонент і заразом рефакторимо ці 3 місця на нього).
- Сторінки, що отримують повноцінний loading-gate (спінер на час
  початкового завантаження) + error-банер (не ховає вже завантажені дані,
  якщо впала лише частина запитів):
  - **Overview** (`useDashboardData`: servers/ping/downtime/backups/zabbix)
  - **Backups** (`useBackupsData` — `loading`/`error` вже повертає, просто
    не використовується)
  - **Ping** (`useServers`/`usePingStream`)
  - **Resources** (`useServers`/`usePingStream`)
  - **Uptime** (`useServers`/`useDowntimeData`/`usePingStream`)
  - Заразом (не називалось прямо, але випливає з п. 1.1/1.2): **Zabbix
    Alerts** і **RDP Sessions** — щойно їхні хуки самі почнуть повертати
    `loading`/`error`, сторінки мають це показувати для консистентності.

---

## Пріоритет 2 — Мертві елементи ✅ ЗАВЕРШЕНО (2026-08-22)

- [x] `RecentActivity` "View all activity" → `navigate('/logs')`.
- [x] `AttentionRequired` — весь header перетворено на `<button>` (краще для
  a11y/клавіатури, ніж onClick на div) → `navigate('/zabbix-alerts')`,
  cursor:pointer тепер відповідає дійсності.
- [x] `MaintenanceCard` — кнопку "Schedule maintenance" видалено разом із
  тепер-невикористаним `.action`-класом у SCSS.
- [x] `UptimeCard` (Overview) — декоративний тригер "24h" замінено на
  статичний текстовий лейбл (той самий підхід, що вже застосований до
  System Resources — чесно, без фальшивої інтерактивності).

---

## Пріоритет 3 — Втрачені фічі з WPF ✅ ЗАВЕРШЕНО (2026-08-22)

### 3.1 Дії на вкладці Ping (Restart / Shutdown / Start RDP / Continuous Ping)

Розвідка перед плануванням показала важливу деталь — бекенд-логіка
**частково вже існує**, просто ніколи не була підключена до REST API:

- `AdminConsole.Infrastructure/Remote/RemoteManagementService.cs` вже
  зареєстрований у DI (`Program.cs`), містить:
  - `RemoteRestartAsync` / `RemoteShutdownAsync` — реальний WMI-виклик
    (`Win32_OperatingSystem.Win32Shutdown`) проти **віддаленого** сервера.
    Це працює коректно і для headless Windows Service (WMI йде по мережі
    від імені `DOMAIN\svc_adminconsole`, жодного локального процесу не
    відкриває) — просто бракує контролера.
  - `OpenContinuousPingAsync` / `OpenRdpAsync` / `OpenSshAsync` — **НЕ
    придатні для вебу як є**: викликають `Process.Start(cmd.exe/mstsc.exe/putty.exe)`
    на машині, де крутиться сама служба. Для Windows Service це Session 0
    isolation — інтерактивного робочого стола там немає, вікно нікому не
    покажеться (і навіть якби показалось — не на комп'ютері адміна, а на
    сервері). Потрібна принципово інша, веб-нативна реалізація:
    - **Start RDP** → бекенд генерує `.rdp`-файл (`full address:s:<ip>`,
      `username:s:...` тощо), браузер скачує — локальний RDP-клієнт
      адміна відкриває його сам. Стандартний веб-патерн для цього класу задач.
    - **Continuous Ping** → живий стрім пінгу прямо в UI (модалка/панель,
      що раз на секунду опитує новий ендпоінт або підписується на
      SignalR-стрім для одного хоста), замість відкриття `cmd.exe`.
  - `OpenSshAsync` користувач не згадував у запиті — залишається поза
    скоупом Пріоритету 3, хоча код так само існує.
**Рішення користувача (2026-08-22):**
- **Авторизація** — спільна `Viewer`-політика (`SANTA\AdminConsole-Admins`),
  окрему `Admin`-політику не заводимо. Обґрунтування: сама AD-група вже
  складається лише з довірених адмінів, а не випадкових глядачів.
- **Підтвердження** — однакова модалка для Restart і Shutdown ("Ви
  впевнені? Restart/Shutdown сервера X"), без посиленого typed-confirm для
  Shutdown окремо.
- **Continuous Ping** — модалка з живим пінгом прямо в браузері: клік на
  кнопку відкриває модальне вікно, що раз на секунду пінгує обраний хост і
  показує список результатів/латентність; зупиняється, коли модалку
  закрито. Реалізується через уже готовий `GET /api/ping` (без нового
  бекенд-ендпоінта) — фронтенд просто фільтрує відповідь до одного хоста
  й опитує повторно, поки модалка відкрита.
- **SSH** лишається поза скоупом (код у `RemoteManagementService.OpenSshAsync`
  існував, але користувач його не запитував — видалено разом з `OpenRdpAsync`/
  `OpenContinuousPingAsync`, замінені на веб-нативні реалізації нижче).

**Реалізовано:**
- `RemoteManagementService.RemoteRestartAsync`/`RemoteShutdownAsync` тепер
  повертають `(bool Success, string? Error)` замість fire-and-forget —
  REST одразу повідомляє результат, а не лише "запит відправлено".
  `OpenContinuousPingAsync`/`OpenRdpAsync`/`OpenSshAsync` видалені (непридатні
  для headless-служби, замінені веб-нативними підходами нижче).
- `ServersController` — `POST /api/servers/{ip}/restart`, `POST /api/servers/{ip}/shutdown`
  (обидва: 404 якщо ip не в конфігурації, 400 якщо не Windows-сервер),
  `GET /api/servers/{ip}/rdp-file` (.rdp-файл на скачування, без хардкоджених
  credentials — Windows сам запитає логін).
- Фронтенд: нова переюзабельна `Modal` (React portal), кнопки Restart/
  Shutdown/RDP/Continuous Ping у `PingHostsTable` (Windows-only для перших
  трьох, Continuous Ping — для всіх типів), єдина confirm-модалка з
  розгортанням у результат (success/error) на місці, нова `ContinuousPingModal`
  (живий пінг раз/сек через `GET /api/ping`, зупиняється при закритті).

### 3.2 SLA-звіт на вкладці Uptime

Розвідка: бекенд **повністю готовий і вже працює**:

- `SlaController` — `GET /api/sla` (JSON) і `GET /api/sla/html`
  (самодостатній HTML-файл, той самий рендер, що й у щотижневій
  Hangfire-джобі `SlaReportJob`).
- `SlaReportService` / `SlaReportHtmlRenderer` — уся бізнес-логіка
  розрахунку вже перенесена й перевірена.

**Рішення користувача (2026-08-22):** вбудована секція прямо на сторінці
Uptime (не модалка) — форма (діапазон дат + опційний фільтр group/server) +
кнопка "Generate SLA Report", результат показується таблицею тут же, плюс
кнопка "Download HTML" поруч.

**Реалізовано:** новий `SlaReportSection` на Uptime — форма з дефолтним
діапазоном "останні 7 днів", таблиця по серверах (Uptime%/Downtime/
Incidents/MTTR — новий `formatTimeSpan` для .NET `TimeSpan` "c"-формату,
перевірено емпірично, що це саме такий формат за замовчуванням у .NET 8
System.Text.Json), посилання "Download HTML" на готовий `/api/sla/html`.

---

## Порядок виконання

Пріоритет 1 → Пріоритет 2 → Пріоритет 3 — **усе завершено**.
