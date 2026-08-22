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
 * Open incidents always come first (newest first), resolved ones follow,
 * newest to oldest (Step 5, #3). UptimeUpdatedOccurred fires after every
 * change (delete/clear) with the same full snapshot, so the list re-sorts
 * itself automatically — no separate refetch needed.
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
 * Step 6 (#10): search by server name + date range. Unlike Logs, there's no
 * separate backend request here — useDowntimeData() already holds the full
 * snapshot (scale is hundreds of records, not millions), so filtering is
 * purely client-side, without debounce or refetch.
 */
function filterIncidents(records: DowntimeRecord[], search: string, from: string, to: string) {
  const needle = search.trim().toLowerCase()
  const fromMs = from ? new Date(from).getTime() : null
  // "to" is inclusive of the entire selected day.
  const toMs = to ? new Date(to).getTime() + 24 * 60 * 60 * 1000 : null

  return records.filter((r) => {
    if (needle && !r.serverName.toLowerCase().includes(needle)) return false
    const fellAtMs = new Date(r.fellAt).getTime()
    if (fromMs !== null && fellAtMs < fromMs) return false
    if (toMs !== null && fellAtMs >= toMs) return false
    return true
  })
}

/** Composition for the Uptime page: servers + downtime (REST+SignalR) + ping (only for live status in the table). */
export function useUptimePageViewModel() {
  const serversQuery = useServers()
  const downtimeQuery = useDowntimeData()
  const pingQuery = usePingStream()
  const [incidentActionBusy, setIncidentActionBusy] = useState(false)
  const [incidentActionError, setIncidentActionError] = useState<ApiError | null>(null)
  const [incidentSearch, setIncidentSearch] = useState('')
  const [incidentFrom, setIncidentFrom] = useState('')
  const [incidentTo, setIncidentTo] = useState('')

  // Audit step 11.3: the initial load (servers/downtime/ping) used to have
  // neither a loading indicator nor error display — the page would silently
  // show zeros, indistinguishable from "there's genuinely no data".
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
      // On success, UptimeUpdatedOccurred arrives via SignalR and updates downtimeQuery.records on its own.
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
