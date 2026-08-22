import { PingStatus, type PingResult, type ServerEntry, type ServerType } from '@/lib/api/types'

export interface GlobalPingStats {
  online: number
  offline: number
  total: number
  successRate: number
  avgLatencyMs: number | null
  hasData: boolean
}

/** Aggregated stats across all hosts from the latest PingBatchResultOccurred. */
export function computeGlobalPingStats(servers: ServerEntry[], results: PingResult[]): GlobalPingStats {
  const hasData = results.length > 0
  const total = results.length || servers.length
  const online = results.filter((r) => r.status === PingStatus.Online).length
  const offline = results.filter((r) => r.status === PingStatus.Offline).length
  const successRate = total > 0 ? Math.round((online / total) * 100) : 0

  const latencies = results
    .filter((r) => r.status === PingStatus.Online && r.latencyMs != null)
    .map((r) => r.latencyMs as number)
  const avgLatencyMs = latencies.length > 0 ? Math.round(latencies.reduce((a, b) => a + b, 0) / latencies.length) : null

  return { online, offline, total, successRate, avgLatencyMs, hasData }
}

export interface HostRow {
  ip: string
  name: string
  group: string
  type: ServerType
  status: PingStatus
  latencyMs: number | null
  lastChecked: string | null
}

/**
 * Merges the static server config (/api/servers) with the latest
 * ping snapshot by IP — hosts that haven't received any event yet
 * stay in the Unknown state (an honest "not checked yet") instead of
 * disappearing from the table.
 */
export function mergeServersWithPingResults(servers: ServerEntry[], results: PingResult[]): HostRow[] {
  const byIp = new Map(results.map((r) => [r.ip, r]))

  return servers.map((server) => {
    const result = byIp.get(server.ip)
    return {
      ip: server.ip,
      name: server.name,
      group: server.group,
      type: server.type,
      status: result?.status ?? PingStatus.Unknown,
      latencyMs: result?.latencyMs ?? null,
      lastChecked: result?.lastChecked ?? null,
    }
  })
}
