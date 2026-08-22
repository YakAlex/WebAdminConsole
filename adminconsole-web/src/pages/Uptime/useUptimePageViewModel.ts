import { useState } from 'react'
import { useServers } from '@/hooks/dashboard/useServers'
import { usePingStream } from '@/hooks/dashboard/usePingStream'
import { useDowntimeData } from '@/hooks/dashboard/useDowntimeData'
import { computeUptimeAxisLabels, computeUptimeSeries, countIncidentsInWindow } from '@/hooks/dashboard/uptimeMath'
import { clearResolvedDowntime, deleteDowntimeRecord } from '@/lib/api/endpoints'
import { ApiError } from '@/lib/api/http'
import { PingStatus, type DowntimeRecord } from '@/lib/api/types'
import type { DetailedDeviceRow } from '@/components/uptime/UptimeDeviceTable/UptimeDeviceTable'

/**
 * Відкриті інциденти — завжди зверху (найновіші спочатку), закриті —
 * нижче, від нових до старих (Крок 5, #3). UptimeUpdatedOccurred після
 * кожної зміни (delete/clear) прилітає з тим самим повним знімком, тож
 * список пересортовується автоматично — окремого рефетчу не потрібно.
 */
function sortIncidents(records: DowntimeRecord[]) {
  return [...records].sort((a, b) => {
    const aOpen = a.recoveredAt === null
    const bOpen = b.recoveredAt === null
    if (aOpen !== bOpen) return aOpen ? -1 : 1
    return new Date(b.fellAt).getTime() - new Date(a.fellAt).getTime()
  })
}

/**
 * Крок 6 (#10): пошук за іменем сервера + діапазон дат. На відміну від
 * Logs, тут немає окремого запиту до бекенду — useDowntimeData() і так
 * тримає повний знімок (масштаб — сотні записів, не мільйони), тож
 * фільтрація суто клієнтська, без дебаунсу/рефетчу.
 */
function filterIncidents(records: DowntimeRecord[], search: string, from: string, to: string) {
  const needle = search.trim().toLowerCase()
  const fromMs = from ? new Date(from).getTime() : null
  // "to" — включно весь обраний день.
  const toMs = to ? new Date(to).getTime() + 24 * 60 * 60 * 1000 : null

  return records.filter((r) => {
    if (needle && !r.serverName.toLowerCase().includes(needle)) return false
    const fellAtMs = new Date(r.fellAt).getTime()
    if (fromMs !== null && fellAtMs < fromMs) return false
    if (toMs !== null && fellAtMs >= toMs) return false
    return true
  })
}

/** Композиція для сторінки Uptime: servers + downtime (REST+SignalR) + ping (лише для live-статусу в таблиці). */
export function useUptimePageViewModel() {
  const serversQuery = useServers()
  const downtimeQuery = useDowntimeData()
  const pingQuery = usePingStream()
  const [incidentActionBusy, setIncidentActionBusy] = useState(false)
  const [incidentActionError, setIncidentActionError] = useState<ApiError | null>(null)
  const [incidentSearch, setIncidentSearch] = useState('')
  const [incidentFrom, setIncidentFrom] = useState('')
  const [incidentTo, setIncidentTo] = useState('')

  // Крок 11.3 аудиту: початкове завантаження (servers/downtime/ping) раніше
  // не мало ні loading-індикатора, ні відображення помилки — сторінка просто
  // мовчки показувала нулі, невідрізнимо від "даних справді нема".
  const initialLoading = serversQuery.loading || downtimeQuery.loading || pingQuery.loading
  const initialErrors = [
    serversQuery.error && { context: 'servers', error: serversQuery.error },
    downtimeQuery.error && { context: 'uptime history', error: downtimeQuery.error },
    pingQuery.error && { context: 'ping', error: pingQuery.error },
  ].filter((x): x is { context: string; error: ApiError } => x !== null)

  const pingResults = pingQuery.payload?.results ?? []
  const overall = computeUptimeSeries(downtimeQuery.records, serversQuery.servers.length, { hours: 24, buckets: 24 })
  const axisLabels = computeUptimeAxisLabels(24, 5)
  const incidentsInWindow = countIncidentsInWindow(downtimeQuery.records, 24)

  const deviceRows: DetailedDeviceRow[] = serversQuery.servers.map((server) => {
    const hostPing = pingResults.find((r) => r.ip === server.ip)
    const series = computeUptimeSeries(downtimeQuery.records, 1, { hours: 24, buckets: 8, serverIp: server.ip })

    return {
      ip: server.ip,
      name: server.name,
      group: server.group,
      status: hostPing?.status ?? PingStatus.Unknown,
      uptimePercent: series.percent,
      trend: series.buckets,
      incidentsInWindow: countIncidentsInWindow(downtimeQuery.records, 24, server.ip),
      responseTimeMs: hostPing?.latencyMs ?? null,
      lastCheck: hostPing?.lastChecked ?? null,
    }
  })

  const deleteIncident = async (serverIp: string, fellAt: string) => {
    setIncidentActionBusy(true)
    setIncidentActionError(null)
    try {
      await deleteDowntimeRecord(serverIp, fellAt)
      // Успіх — UptimeUpdatedOccurred прилетить через SignalR і оновить downtimeQuery.records сам.
    } catch (err) {
      setIncidentActionError(err instanceof ApiError ? err : new ApiError(0, 'Unknown error'))
    } finally {
      setIncidentActionBusy(false)
    }
  }

  const clearResolvedIncidents = async () => {
    setIncidentActionBusy(true)
    setIncidentActionError(null)
    try {
      await clearResolvedDowntime()
    } catch (err) {
      setIncidentActionError(err instanceof ApiError ? err : new ApiError(0, 'Unknown error'))
    } finally {
      setIncidentActionBusy(false)
    }
  }

  return {
    initialLoading,
    initialErrors,
    overallPercent: overall.percent,
    trend: overall.buckets,
    axisLabels,
    monitoredDevices: serversQuery.servers.length,
    servers: serversQuery.servers,
    incidentsInWindow,
    deviceRows,
    incidents: sortIncidents(filterIncidents(downtimeQuery.records, incidentSearch, incidentFrom, incidentTo)),
    incidentActionBusy,
    incidentActionError,
    deleteIncident,
    clearResolvedIncidents,
    incidentSearch,
    setIncidentSearch,
    incidentFrom,
    setIncidentFrom,
    incidentTo,
    setIncidentTo,
    hasActiveIncidentFilters: Boolean(incidentSearch || incidentFrom || incidentTo),
    clearIncidentFilters: () => {
      setIncidentSearch('')
      setIncidentFrom('')
      setIncidentTo('')
    },
  }
}
