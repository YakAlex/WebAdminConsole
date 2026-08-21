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
  ZabbixProblemsPayload,
  ZabbixTestResult,
} from './types'

export const getServers = () => apiGet<ServerEntry[]>('/api/servers')

/** Живий знімок ping-статусів ЗАРАЗ (реально пінгує сервери на бекенді) — для початкового завантаження сторінки. */
export const getPing = () => apiGet<PingBatchPayload>('/api/ping')

/** Живий знімок активних Zabbix-проблем ЗАРАЗ — Крок 11.1 аудиту (раніше сторінка мала лише SignalR, без REST). */
export const getZabbixProblems = () => apiGet<ZabbixProblemsPayload>('/api/zabbix')

/** Живий знімок RDP-сесій ЗАРАЗ (реально опитує термінальні сервери) — Крок 11.2 аудиту. */
export const getRdpSessions = () => apiGet<RdpSnapshotPayload>('/api/rdp-sessions')

// ── Server actions (Пріоритет 3, #3.1) ──────────────────────────────────────

export const restartServer = (ip: string) => apiPost<ServerActionResult>(`/api/servers/${encodeURIComponent(ip)}/restart`)

export const shutdownServer = (ip: string) => apiPost<ServerActionResult>(`/api/servers/${encodeURIComponent(ip)}/shutdown`)

/** URL для .rdp-файлу — використовується напряму як href="" (той самий origin, Windows-авторизація йде через cookie/negotiate так само, як і звичайна навігація). */
export const rdpFileUrl = (ip: string) => `/api/servers/${encodeURIComponent(ip)}/rdp-file`

// ── SLA Report (Пріоритет 3, #3.2) ──────────────────────────────────────────

export interface SlaReportQuery {
  /** ISO рядок. */
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

/** Той самий готовий HTML-рендер, що й у щотижневій Hangfire-джобі — просто на вимогу. */
export const slaReportHtmlUrl = (query: SlaReportQuery) => `/api/sla/html?${slaParams(query).toString()}`

export const getDowntime = () => apiGet<DowntimeRecord[]>('/api/downtime')

/** Видаляє один закритий інцидент (Крок 5, #3 — аналог WPF UptimeViewModel.DeleteRecord). */
export const deleteDowntimeRecord = (serverIp: string, fellAt: string) =>
  apiDelete(`/api/downtime?serverIp=${encodeURIComponent(serverIp)}&fellAt=${encodeURIComponent(fellAt)}`)

/** Масово видаляє всі закриті інциденти (аналог WPF "Clear History"). Повертає кількість видалених. */
export const clearResolvedDowntime = () => apiDelete<number>('/api/downtime/resolved')

export const getBackups = () => apiGet<BackupCheckState[]>('/api/backups')

export interface LogQuery {
  /** ISO рядок — нижня межа (включно), Крок 6 (#10). */
  from?: string
  /** ISO рядок — верхня межа (виключно). */
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

// ── Settings: Monitoring toggles ────────────────────────────────────────────

export const getMonitoringToggles = () => apiGet<MonitoringToggles>('/api/monitoring/toggles')

export const updateMonitoringToggles = (toggles: MonitoringToggles) =>
  apiPut<MonitoringToggles>('/api/monitoring/toggles', toggles)

// ── Maintenance windows (Аудит-фікс 2026-08-22, п.1) ────────────────────────

export const getMaintenanceWindows = () => apiGet<MaintenanceWindow[]>('/api/maintenance')

export interface StartMaintenanceRequest {
  /** Рівно одне з serverIp/targetGroup. */
  serverIp?: string
  targetGroup?: string
  /** Відсутнє — без обмеження часу (до ручного завершення). */
  durationMinutes?: number
  reason?: string
}

export const startMaintenance = (request: StartMaintenanceRequest) =>
  apiPost<MaintenanceWindow>('/api/maintenance', request)

/** key — MaintenanceWindow.serverIp або "group:{targetGroup}". */
export const endMaintenance = (key: string) => apiDelete(`/api/maintenance?key=${encodeURIComponent(key)}`)
