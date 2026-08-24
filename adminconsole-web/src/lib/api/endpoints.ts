import { apiDelete, apiGet, apiPost, apiPut } from './http'
import type {
  AppLogEntry,
  BackupCheckState,
  CredentialsStatusResponse,
  DowntimeRecord,
  MaintenanceWindow,
  MonitoringToggles,
  PingBatchPayload,
  RdpSnapshotPayload,
  ServerActionResult,
  ServerEntry,
  TelegramAllowedUserView,
  TelegramPendingRequest,
  ZabbixProblemsPayload,
  ZabbixTestResult,
} from './types'

export const getServers = () => apiGet<ServerEntry[]>('/api/servers')

/** Live snapshot of ping statuses RIGHT NOW (actually pings the servers on the backend) — for the page's initial load. */
export const getPing = () => apiGet<PingBatchPayload>('/api/ping')

/** Live snapshot of active Zabbix problems RIGHT NOW — audit step 11.1 (previously the page only had SignalR, no REST). */
export const getZabbixProblems = () => apiGet<ZabbixProblemsPayload>('/api/zabbix')

/** Live snapshot of RDP sessions RIGHT NOW (actually polls the terminal servers) — audit step 11.2. */
export const getRdpSessions = () => apiGet<RdpSnapshotPayload>('/api/rdp-sessions')

// ── Server actions (Priority 3, #3.1) ────────────────────────────────────────

export const restartServer = (ip: string) => apiPost<ServerActionResult>(`/api/servers/${encodeURIComponent(ip)}/restart`)

export const shutdownServer = (ip: string) => apiPost<ServerActionResult>(`/api/servers/${encodeURIComponent(ip)}/shutdown`)

// ── SLA Report (Priority 3, #3.2) ────────────────────────────────────────────

export interface SlaReportQuery {
  /** ISO string. */
  from: string
  to: string
  group?: string
  server?: string
}

function slaParams(query: SlaReportQuery): URLSearchParams {
  const params = new URLSearchParams({ from: query.from, to: query.to })
  if (query.group) params.set('group', query.group)
  if (query.server) params.set('server', query.server)
  return params
}

/** The same pre-rendered HTML that the weekly Hangfire job produces — just on demand. */
export const slaReportHtmlUrl = (query: SlaReportQuery) => `/api/sla/html?${slaParams(query).toString()}`

export const getDowntime = () => apiGet<DowntimeRecord[]>('/api/downtime')

/** Deletes a single closed incident (Step 5, #3 — equivalent to WPF UptimeViewModel.DeleteRecord). */
export const deleteDowntimeRecord = (serverIp: string, fellAt: string) =>
  apiDelete(`/api/downtime?serverIp=${encodeURIComponent(serverIp)}&fellAt=${encodeURIComponent(fellAt)}`)

/** Bulk-deletes all closed incidents (equivalent to WPF "Clear History"). Returns the count deleted. */
export const clearResolvedDowntime = () => apiDelete<number>('/api/downtime/resolved')

export const getBackups = () => apiGet<BackupCheckState[]>('/api/backups')

export interface LogQuery {
  /** ISO string — lower bound (inclusive), Step 6 (#10). */
  from?: string
  /** ISO string — upper bound (exclusive). */
  to?: string
  search?: string
}

export const getLogs = (take = 20, query: LogQuery = {}) => {
  const params = new URLSearchParams({ take: String(take) })
  if (query.from) params.set('after', query.from)
  if (query.to) params.set('before', query.to)
  if (query.search) params.set('search', query.search)
  return apiGet<AppLogEntry[]>(`/api/logs?${params.toString()}`)
}

// ── Settings: Credentials ───────────────────────────────────────────────────

export const getCredentials = () => apiGet<CredentialsStatusResponse>('/api/credentials')

export const saveZabbixToken = (token: string) =>
  apiPost<ZabbixTestResult>('/api/credentials/zabbix/token', { token })

export const clearZabbixCredentials = () => apiDelete('/api/credentials/zabbix')

export const saveTelegramToken = (botToken: string) => apiPost('/api/credentials/telegram', { botToken })

export const clearTelegramCredentials = () => apiDelete('/api/credentials/telegram')

// ── Settings: Telegram Users ────────────────────────────────────────────────

export const getTelegramUsers = () => apiGet<TelegramAllowedUserView[]>('/api/telegramusers')

export const removeTelegramUser = (chatId: number) => apiDelete(`/api/telegramusers/${chatId}`)

// ── Telegram access — claim code + pending requests (Audit fix 2026-08-22, item 2) ──

export interface ClaimCodeResponse {
  code: string
  expiresAt: string
}

/** The admin still has to send /claim_admin <code> in Telegram themselves — this only generates the code. */
export const generateTelegramClaimCode = () => apiPost<ClaimCodeResponse>('/api/telegramusers/claim-code')

export interface TelegramPendingStatus {
  pending: TelegramPendingRequest[]
  isPrimaryAdminClaimed: boolean
}

export const getTelegramPending = () => apiGet<TelegramPendingStatus>('/api/telegramusers/pending')

export const approveTelegramRequest = (id: number) => apiPost(`/api/telegramusers/pending/${id}/approve`)

export const denyTelegramRequest = (id: number) => apiPost(`/api/telegramusers/pending/${id}/deny`)

// ── Settings: Monitoring toggles ────────────────────────────────────────────

export const getMonitoringToggles = () => apiGet<MonitoringToggles>('/api/monitoring/toggles')

export const updateMonitoringToggles = (toggles: MonitoringToggles) =>
  apiPut<MonitoringToggles>('/api/monitoring/toggles', toggles)

// ── Maintenance windows (Audit fix 2026-08-22, item 1) ──────────────────────

export const getMaintenanceWindows = () => apiGet<MaintenanceWindow[]>('/api/maintenance')

export interface StartMaintenanceRequest {
  /** Exactly one of serverIp/targetGroup. */
  serverIp?: string
  targetGroup?: string
  /** Absent — no time limit (until manually ended). */
  durationMinutes?: number
  reason?: string
}

export const startMaintenance = (request: StartMaintenanceRequest) =>
  apiPost<MaintenanceWindow>('/api/maintenance', request)

/** key — MaintenanceWindow.serverIp or "group:{targetGroup}". */
export const endMaintenance = (key: string) => apiDelete(`/api/maintenance?key=${encodeURIComponent(key)}`)
