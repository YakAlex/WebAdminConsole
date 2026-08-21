import { apiDelete, apiGet, apiPost, apiPut } from './http'
import type {
  AppLogEntry,
  BackupCheckState,
  CredentialsStatusResponse,
  DowntimeRecord,
  MonitoringToggles,
  PingBatchPayload,
  ServerEntry,
  TelegramAllowedUserView,
  ZabbixTestResult,
} from './types'

export const getServers = () => apiGet<ServerEntry[]>('/api/servers')

/** Живий знімок ping-статусів ЗАРАЗ (реально пінгує сервери на бекенді) — для початкового завантаження сторінки. */
export const getPing = () => apiGet<PingBatchPayload>('/api/ping')

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

export const addTelegramUser = (chatId: number, username: string) =>
  apiPost('/api/telegramusers', { chatId, username: username || null })

export const removeTelegramUser = (chatId: number) => apiDelete(`/api/telegramusers/${chatId}`)

// ── Settings: Monitoring toggles ────────────────────────────────────────────

export const getMonitoringToggles = () => apiGet<MonitoringToggles>('/api/monitoring/toggles')

export const updateMonitoringToggles = (toggles: MonitoringToggles) =>
  apiPut<MonitoringToggles>('/api/monitoring/toggles', toggles)
