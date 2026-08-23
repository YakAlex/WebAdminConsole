// ============================================================================
// REST/SignalR DTO types — mirror AdminConsole.Domain.Models/Events 1:1.
//
// The backend serializes via the default System.Text.Json (AddControllers()/
// AddSignalR() with no custom JsonSerializerOptions) — this means:
//   - properties are camelCase ("ServerName" → "serverName", "IP" → "ip");
//   - enums are serialized as NUMBERS (no JsonStringEnumConverter), so
//     below each enum is duplicated as a numeric const enum with the
//     same ordering as in C#.
// If the backend ever adds a JsonStringEnumConverter, this is the one
// place that will need updating.
// ============================================================================

// ── Enums (order = order in C#, the values matter!) ─────────────────────────
//
// `enum` is disallowed in this project (tsconfig: erasableSyntaxOnly —
// enums generate non-erasable runtime code). Instead, the standard TS
// replacement is used: a const object "as const" + a union type with
// the same name as the values. Usage stays identical: `PingStatus.Online`.

export const PingStatus = {
  Unknown: 0,
  Online: 1,
  Offline: 2,
  Checking: 3,
} as const
export type PingStatus = (typeof PingStatus)[keyof typeof PingStatus]

export const ServerType = {
  Windows: 0,
  Linux: 1,
  Network: 2,
} as const
export type ServerType = (typeof ServerType)[keyof typeof ServerType]

export const BackupKind = {
  Full: 0,
  Diff: 1,
} as const
export type BackupKind = (typeof BackupKind)[keyof typeof BackupKind]

export const BackupOutcome = {
  Unknown: 0,
  Ok: 1,
  SizeWarning: 2,
  Stale: 3,
  Missing: 4,
} as const
export type BackupOutcome = (typeof BackupOutcome)[keyof typeof BackupOutcome]

export const LogSeverity = {
  Info: 0,
  Success: 1,
  Warning: 2,
  Error: 3,
} as const
export type LogSeverity = (typeof LogSeverity)[keyof typeof LogSeverity]

export const ZabbixSeverity = {
  NotClassified: 0,
  Information: 1,
  Warning: 2,
  Average: 3,
  High: 4,
  Disaster: 5,
} as const
export type ZabbixSeverity = (typeof ZabbixSeverity)[keyof typeof ZabbixSeverity]

export const RdpSessionState = {
  Active: 0,
  Disconnected: 1,
  Idle: 2,
  Unknown: 3,
} as const
export type RdpSessionState = (typeof RdpSessionState)[keyof typeof RdpSessionState]

export const MaintenanceAction = {
  Started: 0,
  Ended: 1,
} as const
export type MaintenanceAction = (typeof MaintenanceAction)[keyof typeof MaintenanceAction]

// ── REST DTOs ────────────────────────────────────────────────────────────

export interface ServerEntry {
  name: string
  ip: string
  group: string
  type: ServerType
}

export interface DowntimeRecord {
  serverName: string
  serverIp: string
  serverGroup: string
  fellAt: string
  recoveredAt: string | null
  closedByMaintenance: boolean
}

export interface BackupSample {
  observedAt: string
  sizeBytes: number
}

export interface BackupCheckState {
  name: string
  host: string
  kind: BackupKind
  outcome: BackupOutcome
  lastConfirmedAt: string | null
  lastConfirmedOutcome: BackupOutcome | null
  consecutiveUnknownCount: number
  consecutiveBadCount: number
  lastRawOutcome: BackupOutcome | null
  lastError: string | null
  history: BackupSample[]
}

export interface AppLogEntry {
  severity: LogSeverity
  source: string
  message: string
  timestamp: string
  formatted: string
}

// ── SignalR event payloads (method name = typeof(T).Name on the backend) ──

export interface PingResult {
  name: string
  ip: string
  group: string
  status: PingStatus
  latencyMs: number | null
  lastChecked: string
}

export interface PingBatchPayload {
  results: PingResult[]
  cycleCompletedAt: string
}

export interface PingBatchResultEvent {
  payload: PingBatchPayload
}

export interface UptimeUpdatedEvent {
  snapshot: DowntimeRecord[]
}

export interface BackupStatusUpdatedEvent {
  snapshot: BackupCheckState[]
}

export interface BackupTransitionEvent {
  serverName: string
  kind: BackupKind
  previous: BackupOutcome
  current: BackupOutcome
}

export interface MaintenanceWindow {
  serverIp: string | null
  targetGroup: string | null
  displayName: string
  from: string
  to: string | null
  reason: string
  createdAt: string
}

export interface MaintenanceChangedEvent {
  action: MaintenanceAction
  window: MaintenanceWindow
}

export interface AppLogEntryEvent {
  entry: AppLogEntry
}

export interface RdpSessionInfo {
  username: string
  sessionName: string
  sessionId: string
  state: RdpSessionState
  idleTime: string
  logonTime: string
  serverName: string
  serverIp: string
}

export interface RdpSessionsPayload {
  serverName: string
  serverIp: string
  sessions: RdpSessionInfo[]
  errorMessage: string | null
  globalDailyPeak: number
  lastLogoutUsername: string | null
  lastLogoutServer: string | null
  lastLogoutAt: string | null
}

export interface RdpSessionsUpdatedEvent {
  payload: RdpSessionsPayload
}

/** POST /api/servers/{ip}/restart|shutdown — result of a WMI command (Priority 3, #3.1). */
export interface ServerActionResult {
  success: boolean
  error: string | null
}

/** GET /api/rdp-sessions — aggregated live snapshot (audit step 11.2). */
export interface RdpSnapshotPayload {
  sessions: RdpSessionInfo[]
  globalDailyPeak: number
  lastLogoutUsername: string | null
  lastLogoutServer: string | null
  lastLogoutAt: string | null
}

export interface ZabbixProblem {
  eventId: string
  hostName: string
  description: string
  severity: ZabbixSeverity
  startTime: string
  ageDisplay: string
}

export interface ZabbixProblemsPayload {
  problems: ZabbixProblem[] | null
  errorMessage: string | null
  fetchedAt: string
}

export interface ZabbixProblemsUpdatedEvent {
  payload: ZabbixProblemsPayload
}

// ── Settings: Credentials + Telegram Users (REST, no SignalR) ──────────────

export interface ZabbixCredentialsStatus {
  hasCredentials: boolean
  usesApiToken: boolean
  maskedSecret: string
  username: string
}

export interface TelegramCredentialsStatus {
  hasCredentials: boolean
  maskedToken: string
}

export interface CredentialsStatusResponse {
  zabbix: ZabbixCredentialsStatus
  telegram: TelegramCredentialsStatus
}

/** Result of an immediate connection check right after Save (POST /api/credentials/zabbix/token|password). */
export interface ZabbixTestResult {
  success: boolean
  version: string | null
  error: string | null
}

/** GET/PUT /api/monitoring/toggles — background service toggles (Settings, Step 4 #7). */
export interface MonitoringToggles {
  rdpMonitoringEnabled: boolean
  zabbixMonitoringEnabled: boolean
  backupMonitoringEnabled: boolean
  /** Minimum ZabbixSeverity to poll for (0=NotClassified .. 5=Disaster). Matches the ZabbixSeverity enum below. */
  zabbixMinSeverity: number
}

export const MonitoredService = {
  Rdp: 0,
  Zabbix: 1,
  Backups: 2,
} as const
export type MonitoredService = (typeof MonitoredService)[keyof typeof MonitoredService]

/** SignalR: MonitoringController publishes this on every toggle change in Settings (audit fix item 4). */
export interface MonitoringToggledEvent {
  service: MonitoredService
  enabled: boolean
}

export interface TelegramAllowedUserView {
  chatId: number
  username: string
}

// ── Telegram access — claim code + pending requests (Audit fix 2026-08-22, item 2) ──

export interface TelegramPendingRequest {
  id: number
  chatId: number
  username: string
  requestedAt: string
}

export const TelegramAccessAction = {
  Approved: 0,
  Denied: 1,
  Revoked: 2,
} as const
export type TelegramAccessAction = (typeof TelegramAccessAction)[keyof typeof TelegramAccessAction]

export interface TelegramAccessRequestEvent {
  request: TelegramPendingRequest
}

export interface TelegramAccessChangedEvent {
  action: TelegramAccessAction
  chatId: number
  username: string | null
}
