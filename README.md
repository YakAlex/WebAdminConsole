# AdminConsole

[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![React](https://img.shields.io/badge/React-19-149ECA?logo=react&logoColor=white)](https://react.dev/)
[![TypeScript](https://img.shields.io/badge/TypeScript-black?logo=typescript&logoColor=white)](https://www.typescriptlang.org/)
[![Vite](https://img.shields.io/badge/Vite-8-646CFF?logo=vite&logoColor=white)](https://vitejs.dev/)
[![Platform](https://img.shields.io/badge/platform-Windows%20Service-0078D6?logo=windows&logoColor=white)](#security--architecture)
[![SignalR](https://img.shields.io/badge/real--time-SignalR-512BD4)](#tech-stack)

**A self-hosted, real-time infrastructure monitoring and management console** — ping, uptime/SLA, RDP session tracking, Zabbix alerts, backup verification, scheduled maintenance, and a Telegram bot, all in one dashboard.

AdminConsole v3 is the full rewrite of a legacy WPF desktop application into a **headless ASP.NET Core Windows Service with a React web front end**. Where the original tool only worked at the console of whichever machine it was installed on, this version runs unattended as a background service and is reachable from any browser on the network — no RDP session, no desktop session, no client install. Every monitoring loop, alerting rule, and management action from the desktop era was ported, and several — live SLA reporting, scheduled maintenance windows, Telegram-based approvals — were rebuilt to be genuinely web-native rather than emulated.

![AdminConsole Overview](overview.png)

---

## Table of Contents

- [Tech Stack](#tech-stack)
- [Key Features](#key-features)
- [Security & Architecture](#security--architecture)
- [Prerequisites & Environment Requirements](#prerequisites--environment-requirements)
- [Configuration (`appsettings.json`)](#configuration-appsettingsjson)
- [Deployment / Setup](#deployment--setup)
- [Project Structure](#project-structure)

---

## Tech Stack

### Backend — `AdminConsole.Api` / `AdminConsole.Infrastructure` / `AdminConsole.Domain`

| Concern | Technology |
|---|---|
| Runtime | ASP.NET Core 8 (`net8.0-windows`), self-hosted Kestrel, runs as a **Windows Service** |
| Data access | Entity Framework Core 8 + **SQLite** (WAL mode) |
| Real-time updates | **SignalR** — a single hub fans domain events out to the browser (ping results, uptime changes, RDP sessions, Zabbix problems, backup status, logs, maintenance state) |
| Background jobs | **Hangfire** (SQLite storage) for scheduled work (backup checks, the weekly SLA report); tight polling loops (ping, RDP, Zabbix, local resources) run as native `BackgroundService`s instead, since Hangfire isn't a fit for sub-minute cadences |
| Domain events | **MediatR** — every monitoring service publishes typed notifications; a single `SignalRBroadcastHandler` and a SQLite log-persistence handler subscribe to all of them |
| Remote management | **WMI** (`System.Management`) for remote restart/shutdown and CPU/RAM/event-log queries; `quser` process invocation (Kerberos-authenticated, no stored credentials) for RDP session polling |
| Secrets at rest | Windows **Data Protection API** (DPAPI-NG) encrypting an `AdminConsoleDb` table — no plaintext credentials on disk |
| Telegram bot | `Telegram.Bot` client running in-process as another `BackgroundService`, sharing the same Singleton monitoring services the API talks to |
| Authentication | Windows Integrated Authentication (**NTLM/Kerberos via Negotiate**), authorized by Active Directory group membership |

### Frontend — `adminconsole-web`

| Concern | Technology |
|---|---|
| Framework | **React 19** + **TypeScript**, built with **Vite** |
| Styling | **SCSS Modules** against a single design-token stylesheet (colors, spacing, typography) — no CSS-in-JS, no component library |
| Real-time | `@microsoft/signalr` client, one shared connection per session, ref-counted per-page group subscriptions |
| Routing | `react-router-dom` |
| Data pattern | Every page composes small `use*` hooks that combine an initial REST snapshot with live SignalR updates, so a page never shows a misleadingly empty state before the first push arrives |

The production build is **embedded directly into the ASP.NET Core host** — `dotnet publish` runs `npm run build` and copies the Vite output straight into `wwwroot`, so the shipped artifact is a single self-contained Windows executable serving both the API and the SPA.

---

## Key Features

### Dashboard & Uptime
A single Overview page rolls up system health, ping success rate, live uptime percentage, recent activity, active backups, RDP sessions, and scheduled maintenance into one glance. The Uptime page tracks every Online↔Offline transition per server with **anti-flapping** (a downtime shorter than a configurable threshold never becomes a recorded incident) and produces **on-demand SLA reports** — per-server uptime %, downtime, incident count, MTTR, and a maintenance appendix — rendered as a self-contained, offline-viewable HTML document opened in a new tab.

### Ping & Server Management
A dual-cadence background loop pings every configured host (a faster recovery loop re-checks only currently-offline hosts, so recovery is detected quickly without hammering healthy servers). From the same table, Windows hosts support:
- **Restart / Shutdown** — a WMI call against the remote machine, confirmed through a modal, with no interactive process spawned on the server (the service runs headless, so there's no desktop session to open a window on).
- **RDP** — generates a ready-to-open `.rdp` file on the fly (target address prefilled, no stored credentials — Windows prompts for them locally), the closest thing to a one-click launch a browser can safely do.
- **Continuous ping** — an in-browser modal that polls the live ping endpoint once a second for as long as it's open, replacing the desktop app's ability to spawn its own terminal window.

### RDP Sessions
Polls terminal servers via `quser` under the service's own Kerberos identity (no credentials stored or prompted per poll), tracks session state transitions (connected / disconnected / resumed), daily peak concurrent sessions, and last-logout history.

### Zabbix Integration
Polls the Zabbix API for active Disaster/High severity problems on a configurable interval, supporting both API-token and legacy username/password authentication with automatic backoff on repeated auth failures. Connection is configured and tested directly from Settings.

### Backup Monitoring
Evaluates each configured backup job against file age and a rolling size baseline, with anti-flapping so a single bad read doesn't flip a job's status. Surfaces per-job size history, total backup size across the fleet, and pushes Telegram alerts the moment a job goes Stale or Missing.

### Maintenance Windows
Start a maintenance window against a single server or an entire group, with duration presets or no time limit. While active, Ping and Backup alerting is suppressed and the corresponding uptime incident is marked as maintenance-related rather than counted against SLA. Windows auto-expire on schedule or can be ended early from the same UI that started them.

### Telegram Bot
A full admin bot living in the same process as the API: a one-time **claim code** binds the first Primary Admin, new users are approved or denied through **inline keyboard buttons** (mirrored in the web Settings page, so either side can act), and the bot pushes real-time alerts for new incidents and backup transitions. Command menu covers live status, offline hosts, open incidents, RDP sessions, maintenance, on-demand ping, and backup state — all reading from the exact same in-memory services the web dashboard uses, so the two are never out of sync.

---

## Security & Architecture

- **Runs as a Windows Service**, not an interactive application — `Microsoft.Extensions.Hosting.WindowsServices` hosts Kestrel directly, with no reverse proxy required.
- **Windows Integrated Authentication** end to end: every request is authenticated via NTLM/Kerberos (`Negotiate`), and access is gated by membership in a configured Active Directory security group — there is no separate login form, password, or session token to manage.
- **No plaintext secrets at rest.** Zabbix tokens and the Telegram bot token are encrypted with the Windows Data Protection API (DPAPI-NG) before being written to SQLite; nothing sensitive lives in `appsettings.json`.
- **Least-privilege remote management.** Restart/shutdown and RDP-session polling run under a dedicated domain service account authenticated via Kerberos — no interactive credentials are stored, prompted for, or transmitted per action.
- **Telegram access is a separate, explicit trust boundary.** New bot users must be approved by the Primary Admin (via Telegram or the web UI, both behind the same AD-authenticated session) before the bot will respond to them; rate limiting and cooldowns apply per chat ID.
- **Domain events, not shared mutable state.** Every monitoring service communicates through typed MediatR notifications; the SignalR layer and the persistent log are just two more subscribers, keeping the web UI, the Telegram bot, and the audit log guaranteed-consistent with each other.

---

## Prerequisites & Environment Requirements

AdminConsole is built specifically for a **Windows + Active Directory** environment — it doesn't run standalone and isn't meant to be cross-platform. Before deploying, make sure the following are in place.

### Domain environment

- The host machine and every server being monitored must be joined to the **same Active Directory domain** (or trusted domains) — both Windows Integrated Authentication for the dashboard and Kerberos-based remote management depend on this.
- An **AD security group** must exist and contain everyone who should be able to open the dashboard (see [`Authorization:ViewerGroup`](#configuration-appsettingsjson) below). There's no separate login form or password — group membership *is* the access control.

### The service account

The Windows Service must run under a **dedicated domain account** (e.g. `CONTOSO\svc-adminconsole`) — never `LocalSystem` or `NetworkService`, since those authenticate as the *computer* account and typically have no rights on the servers being managed. This account needs:

- **Local Administrator rights on every managed Windows server** (or, at minimum, WMI `CIMV2` namespace permissions with remote-enable and method-execution rights) — required for the `Win32_OperatingSystem.Win32Shutdown` calls behind Restart/Shutdown.
- **Rights to query session state via `quser`** on servers in the `"Terminal Servers"` group — in practice this is already covered by the local-admin membership above.
- No Kerberos delegation configuration is required beyond a normal domain trust: the service authenticates to remote servers directly under its own identity (a single hop), not on behalf of the browser user viewing the dashboard.

### Firewall & Windows components on *target* servers

Each monitored server needs a few inbound rules enabled — all are predefined Windows Firewall groups, several of which are **off by default** on a clean Windows Server install:

| Requirement | Why it's needed |
|---|---|
| **Windows Management Instrumentation (WMI-In)** | Remote restart/shutdown, plus the underlying DCOM/RPC channel (TCP 135 + dynamic RPC ports) that WMI itself needs |
| **File and Printer Sharing** (named-pipe / RPC access) | `quser` reads Terminal Services session state over named pipes |
| **File and Printer Sharing (Echo Request — ICMPv4-In)** | The ping monitor uses real ICMP; this rule is frequently disabled by default and is the most common cause of a perfectly healthy server showing as "Offline" |
| Remote Desktop Services role | Only for hosts placed in the `"Terminal Servers"` group — `quser` has nothing to report without it |

### The AdminConsole host itself

- Ships **self-contained** (see [Deployment](#deployment--setup)) — no separate .NET runtime install is needed on the host.
- Needs its own **inbound firewall rule** for whatever port Kestrel is configured to listen on (`Kestrel:Endpoints:Http:Url`) if the dashboard will be reached from other machines rather than just `localhost`.

---

## Configuration (`appsettings.json`)

Everything the service needs to run lives in one `appsettings.json`, deployed alongside the executable. It's **deliberately excluded** from the publish/robocopy step (see [Deployment](#deployment--setup)) so redeploying a new build never overwrites a live server's configuration.

```jsonc
{
  "Kestrel": {
    "Endpoints": {
      "Http": { "Url": "http://*:5000" }
    }
  },

  "Authorization": {
    // AD security group allowed to open the dashboard — "DOMAIN\\GroupName".
    "ViewerGroup": "CONTOSO\\AdminConsole-Admins"
  },

  "ConnectionStrings": {
    "AdminConsoleDb": "Data Source=adminconsole.db;Cache=Shared"
  },

  "Monitoring": {
    "PingIntervalSeconds": 30,
    "OfflinePingIntervalSeconds": 10,
    "ZabbixUrl": "https://zabbix.contoso.local",
    "ZabbixPollIntervalSeconds": 60,
    "RdpPollIntervalSeconds": 120,
    "MinIncidentDurationSeconds": 10,
    "BackupPollIntervalMinutes": 60
  },

  "Servers": [
    { "Name": "APP01", "IP": "10.0.1.10", "Group": "Application Servers", "Type": "Windows" },
    { "Name": "TS01",  "IP": "10.0.1.20", "Group": "Terminal Servers",    "Type": "Windows" },
    { "Name": "web01", "IP": "10.0.1.30", "Group": "Web Servers",         "Type": "Linux" },
    { "Name": "SW-01", "IP": "10.0.1.1",  "Group": "Network",             "Type": "Network" }
  ],

  "BackupChecks": [
    {
      "Name": "APP01 SQL Backup",
      "Host": "APP01",
      "Path": "\\\\APP01\\Backups\\SQL",
      "FullPattern": "*_full_*.bak",
      "DiffPattern": "*_diff_*.bak",
      "MaxAgeHoursFull": 26,
      "MaxAgeHoursDiff": 26,
      "SizeWarningThresholdPct": 30
    }
  ]
}
```

- **`Servers`** drives every per-host feature — Ping, Uptime, and which action buttons appear: `"Type": "Windows"` unlocks Restart/Shutdown/RDP, while `"Linux"` and `"Network"` entries get ping-only monitoring. Only servers placed in the `"Terminal Servers"` group are polled for RDP sessions.
- **`BackupChecks`** — one entry per job. `Path` accepts either a local or a UNC path; `DiffPattern` can be left as an empty string for a server that has no differential backups to track.
- **No credentials live in this file.** The Zabbix API token and the Telegram bot token are entered through the web **Settings** page after the service is already running, and are encrypted at rest with the Windows Data Protection API (DPAPI-NG) before being written to SQLite — `appsettings.json` never sees them.

---

## Deployment / Setup

The application ships as a self-contained, single-folder Windows deployment — no separate IIS site, no Node.js on the target machine, no manual frontend build step.

1. **Build the release artifact**

   ```powershell
   .\publish.ps1
   ```

   This runs `dotnet publish` for `AdminConsole.Api` and `AdminConsole.Migration` (self-contained, `win-x64`), which in turn builds the React frontend and embeds it into `wwwroot` automatically, into a single `publish/` folder.

2. **Deploy to the target server**, preserving the live database and configuration:

   ```powershell
   Stop-Service AdminConsole

   robocopy "publish" "C:\Program Files\AdminConsole" /E `
     /XF appsettings.json appsettings.*.json adminconsole.db* hangfire.db*
   ```

3. **Apply pending database migrations** — `adminconsole.db` (EF Core / application data) is a separate SQLite file from `hangfire.db` (background job storage); only the former needs migrating, via the bundled console tool:

   ```powershell
   cd "C:\Program Files\AdminConsole"
   .\AdminConsole.Migration.exe
   ```

   The migration tool is idempotent — safe to run on every deploy even when there's nothing new to apply.

4. **Start the service:**

   ```powershell
   Start-Service AdminConsole
   ```

Configuration (`appsettings.json`) lives outside the publish artifact by design — server list, monitoring intervals, the authorized AD group, and backup job definitions are edited in place on the target machine and are never overwritten by a redeploy.

---

## Project Structure

```
AdminConsole_v3/
├── AdminConsole.Api/              REST controllers, SignalR hub, composition root (Program.cs)
├── AdminConsole.Domain/           Domain models, MediatR events, repository interfaces
├── AdminConsole.Infrastructure/   Background services, EF Core, Telegram bot, WMI/remote management
├── AdminConsole.Migration/        One-time legacy-data import + EF Core migration runner
├── AdminConsole.Tests/            Unit tests
├── adminconsole-web/              React + TypeScript + Vite frontend
└── publish.ps1                    Build script — produces the self-contained deployment artifact
```
