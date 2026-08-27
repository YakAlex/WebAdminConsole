# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

AdminConsole is a self-hosted, real-time infrastructure monitoring console: ping/uptime/SLA, RDP session tracking, Zabbix alerts, backup verification, maintenance windows, and a Telegram bot. It runs as a headless ASP.NET Core **Windows Service** (Kestrel, no IIS/reverse proxy) with an embedded React SPA, authenticated via Windows Integrated Auth (Negotiate) against an AD group — there is no login form and no separate credential store for the dashboard itself. See [README.md](README.md) for the full architecture writeup (event flow diagram, per-service deep dives, security model) — it is extensive and authoritative; this file only orients you and covers what the README doesn't (day-to-day commands).

## Commands

Backend:
```powershell
dotnet run --project AdminConsole.Api    # Kestrel on http://localhost:5074 in dev
dotnet test                               # full xUnit suite (114 tests / 22 files)
dotnet test --filter "FullyQualifiedName~UptimeTrackerServiceTests"   # single class
dotnet test --filter "FullyQualifiedName~Namespace.Class.MethodName" # single test
```

Frontend (`adminconsole-web/`):
```powershell
npm install
npm run dev       # Vite dev server, proxies /api and /hubs to Kestrel (see below)
npm run lint      # oxlint
npm run build     # production build; also runs automatically during `dotnet publish -c Release`
npm run preview   # serve the last build output locally
```

Run backend and frontend as two separate processes for dev — instant backend rebuilds plus Vite HMR. The browser talks to two origins, so `vite.config.ts` proxies `/api` and `/hubs` (`ws: true` for the SignalR upgrade) through to Kestrel over a **shared keep-alive agent** — this is required, not incidental: Negotiate auth is a multi-step handshake bound to one TCP connection, and a non-keep-alive proxy makes every request look like a new anonymous client, so Kestrel repeats the 401 challenge forever instead of completing the handshake. If you touch the proxy config, preserve the shared `Agent({ keepAlive: true })`.

Release build: `.\publish.ps1` (self-contained `win-x64`, hash-verified merge of two staged outputs — see [Central Package Management](README.md#central-package-management) before touching `Directory.Packages.props`, `Directory.Build.props`, or either project's framework references).

## Conventions

Backend (C#): `PascalCase` everywhere, `sealed` classes/records for models, immutable `init`-only properties. Domain events are named `<Subject><PastTenseVerb>Occurred` (`PingBatchResultOccurred`, `MaintenanceChangedOccurred`, ...) and live in `AdminConsole.Domain/Events/`, one file per event. Repositories are `<Noun>Repository` in `AdminConsole.Infrastructure/Data/Repositories/`; controllers are `<Noun>Controller` in `AdminConsole.Api/Controllers/` — both one class per file, named after the thing they own, not the operation.

Frontend (TypeScript/React): one folder per component under `components/<area>/<ComponentName>/`, containing `ComponentName.tsx` + `ComponentName.module.scss` + an `index.ts` barrel export — never a loose `.tsx` file outside a matching folder. Pages follow the same shape under `pages/<PageName>/`, each typically pairing `<PageName>.tsx` with a `use<PageName>ViewModel.ts` hook that owns that page's data/state. Data hooks live in `hooks/` (or `hooks/dashboard/` for the Overview page's per-card hooks) and are always named `use<Noun>` — `useServers`, `useDowntimeData`, `usePingStream`.

## Architecture (the part that spans files)

**Everything flows through MediatR domain events, one-way.** Every background service publishes typed `INotificationHandler<T>` notifications instead of calling other services or the hub directly; `SignalRBroadcastHandler`, `AppLogPersistenceHandler`, and `TelegramBotService` all subscribe independently to the same events. This is why the dashboard, the bot, and the audit log never disagree — they read the same events rather than polling independent state. Follow this pattern for any new monitoring source. Full event-to-subscriber table: [README § Domain Events](README.md#domain-events-mediatr).

**Two scheduling models, chosen deliberately** — don't move work between them without a reason. Tight sub-minute polling loops (ping, uptime, Zabbix, RDP) are plain `BackgroundService`s with their own `Task.Delay` loops; Hangfire (SQLite-backed) owns only the jobs that tolerate a loose clock (`BackupMonitorJob`, weekly `SlaReportJob`, daily `AppLogRetentionJob`). Hangfire job registration goes through `IRecurringJobManager` (DI-scoped), not the static `RecurringJob` facade, which would read an unpopulated `JobStorage.Current`.

**Two separate SQLite databases**, both WAL mode: `adminconsole.db` (EF Core — app data, via `AdminConsoleDbContext`) and `hangfire.db` (Hangfire's own storage). Keep them separate; don't route Hangfire state through the app's `DbContext` or vice versa.

**Project dependency direction is one-way:** `AdminConsole.Domain` (framework-free — no EF Core, no ASP.NET Core; plain models, MediatR event types, repository interfaces) ← `AdminConsole.Infrastructure` (implements the repositories, background services, Telegram bot, WMI/`quser` remote management, EF Core) ← `AdminConsole.Api` (controllers, `DashboardHub`, composition root in `Program.cs`). `AdminConsole.Migration` is a standalone one-time data-import + migration-runner console app. Never introduce a reference the other direction (e.g. Domain depending on Infrastructure).

**Concurrency is deliberate, not incidental.** `PingMonitorService`, `UptimeTrackerService`, and `RdpMonitorService` mix CAS (`ConcurrentDictionary` + `TryUpdate`), per-server `SemaphoreSlim`s, and a couple of plain `lock`s — each one fixes a specific race found in production. Read [README § Concurrency & Thread-Safety](README.md#concurrency--thread-safety) before touching locking in these three services; a "simplification" there is likely reintroducing a known bug.

**Every `BackgroundService.ExecuteAsync` and every `INotificationHandler` with a real failure mode (DB I/O, external calls) must have its own top-level try/catch** that logs `AppLogEntryOccurred.Error` and swallows anything but `OperationCanceledException`. This is load-bearing: an unhandled exception in a `BackgroundService` terminates the whole host by default, and an unhandled exception in a notification handler can propagate back into the publisher's own loop guard. New services/handlers must follow this pattern.

**Authentication has no login form.** Windows Integrated Auth (Negotiate/Kerberos) plus `WindowsGroupClaimsTransformation` maps AD group membership into a `ClaimTypes.Role` claim on every request (required because `WindowsIdentity.RoleClaimType` is immutably `GroupSid` under Kestrel+Negotiate, unlike under IIS). Both REST controllers (via `AdminConsoleControllerBase`) and `DashboardHub` are gated by the same `Viewer` policy. If the DC is unreachable, membership is "cannot confirm" → ordinary 403, never a crash.

**Frontend data pattern:** every page composes `use*` hooks (`adminconsole-web/src/hooks/`) that combine an initial REST snapshot with live SignalR updates, ref-counted per page (a page joins its SignalR group on mount, leaves on unmount). New pages should follow this snapshot+subscribe pattern rather than polling.

**Central Package Management is load-bearing, not stylistic.** `Directory.Packages.props` (transitive pinning) and `Directory.Build.props` (`RuntimeFrameworkVersion` pin) exist because `AdminConsole.Api` and `AdminConsole.Migration` resolve some shared assemblies from different provenances, producing non-identical DLLs that `publish.ps1`'s hash check hard-fails on. Read [README § Central Package Management](README.md#central-package-management) before adding a `FrameworkReference` or touching either props file.

**English-only interface** is a deliberate, standing choice for this internally-facing tool — all user-facing text (UI copy, log messages, Telegram replies, error strings) stays in English regardless of the team's working language.
