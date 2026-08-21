import { useServers } from '@/hooks/dashboard/useServers'
import { usePingStream } from '@/hooks/dashboard/usePingStream'
import { computeGlobalPingStats, mergeServersWithPingResults } from '@/hooks/dashboard/pingMath'
import type { ApiError } from '@/lib/api/http'

/**
 * Композиція для сторінки Ping: лише те, що їй реально треба (без backups/rdp/zabbix/logs — Overview туди не тягнемо).
 * Крок 11.3 аудиту: servers/ping loading+error раніше повністю відкидались.
 */
export function usePingPageViewModel() {
  const serversQuery = useServers()
  const pingQuery = usePingStream()

  const results = pingQuery.payload?.results ?? []
  const stats = computeGlobalPingStats(serversQuery.servers, results)
  const hosts = mergeServersWithPingResults(serversQuery.servers, results)

  const loading = serversQuery.loading || pingQuery.loading
  const errors = [
    serversQuery.error && { context: 'servers', error: serversQuery.error },
    pingQuery.error && { context: 'ping', error: pingQuery.error },
  ].filter((x): x is { context: string; error: ApiError } => x !== null)

  return { stats, hosts, loading, errors }
}
