// ============================================================================
// DTO-типи REST/SignalR — дзеркалять AdminConsole.Domain.Models/Events 1:1.
//
// Бекенд серіалізує через дефолтний System.Text.Json (AddControllers()/
// AddSignalR() без кастомних JsonSerializerOptions) — це означає:
//   - властивості camelCase ("ServerName" → "serverName", "IP" → "ip");
//   - enum'и як ЧИСЛА (немає JsonStringEnumConverter), тому нижче кожен
//     enum продубльований як числовий const enum з тим самим порядком,
//     що й у C#.
// Якщо бекенд колись додасть JsonStringEnumConverter — це єдине місце,
// яке треба буде поправити.
// ============================================================================

// ── Enums (порядок = порядок у C#, значення важливі!) ──────────────────────
//
// `enum` заборонений цим проєктом (tsconfig: erasableSyntaxOnly — enum'и
// генерують нестрипабельний рантайм-код). Замість нього — стандартна
// TS-заміна: const-об'єкт "as const" + union-тип з тим самим іменем, що й
// значення. Використання лишається ідентичним: `PingStatus.Online`.

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

// ── SignalR event payloads (назва методу = typeof(T).Name на бекенді) ─────

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

export interface ResourceSnapshot {
  cpuPercent: number
  ramUsedGb: number
  ramTotalGb: number
  ramPercent: number
  timestamp: string
}

export interface ResourceSnapshotUpdatedEvent {
  snapshot: ResourceSnapshot
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

/** POST /api/servers/{ip}/restart|shutdown — результат WMI-команди (Пріоритет 3, #3.1). */
export interface ServerActionResult {
  success: boolean
  error: string | null
}

/** GET /api/rdp-sessions — агрегований живий знімок (Крок 11.2 аудиту). */
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

// ── Settings: Credentials + Telegram Users (REST, без SignalR) ─────────────

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

/** Результат негайної перевірки з'єднання одразу після Save (POST /api/credentials/zabbix/token|password). */
export interface ZabbixTestResult {
  success: boolean
  version: string | null
  error: string | null
}

/** GET/PUT /api/monitoring/toggles — вмикачі фонових сервісів (Settings, Крок 4 #7). */
export interface MonitoringToggles {
  rdpMonitoringEnabled: boolean
  zabbixMonitoringEnabled: boolean
  backupMonitoringEnabled: boolean
}

export const MonitoredService = {
  Rdp: 0,
  Zabbix: 1,
  Backups: 2,
} as const
export type MonitoredService = (typeof MonitoredService)[keyof typeof MonitoredService]

/** SignalR: MonitoringController публікує це на кожну зміну тумблера в Settings (аудит-фікс п.4). */
export interface MonitoringToggledEvent {
  service: MonitoredService
  enabled: boolean
}

export interface TelegramAllowedUserView {
  chatId: number
  username: string
}
