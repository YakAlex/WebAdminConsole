import { PingStatus, type PingResult, type ServerEntry } from '@/lib/api/types'

export interface GlobalPingStats {
  online: number
  offline: number
  total: number
  successRate: number
  avgLatencyMs: number | null
  hasData: boolean
}

/** Агреговані показники по всіх хостах з останнього PingBatchResultOccurred. */
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
  status: PingStatus
  latencyMs: number | null
  lastChecked: string | null
}

/**
 * Об'єднує статичний конфіг серверів (/api/servers) з останнім ping-знімком
 * по IP — хости, для яких ще не прийшла жодна подія, лишаються у стані
 * Unknown (чесний "ще не перевірено"), а не зникають з таблиці.
 */
export function mergeServersWithPingResults(servers: ServerEntry[], results: PingResult[]): HostRow[] {
  const byIp = new Map(results.map((r) => [r.ip, r]))

  return servers.map((server) => {
    const result = byIp.get(server.ip)
    return {
      ip: server.ip,
      name: server.name,
      group: server.group,
      status: result?.status ?? PingStatus.Unknown,
      latencyMs: result?.latencyMs ?? null,
      lastChecked: result?.lastChecked ?? null,
    }
  })
}
