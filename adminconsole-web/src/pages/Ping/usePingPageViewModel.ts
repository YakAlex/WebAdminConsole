import { useServers } from '@/hooks/dashboard/useServers'
import { usePingStream } from '@/hooks/dashboard/usePingStream'
import { useMaintenanceWindows } from '@/hooks/dashboard/useMaintenanceWindows'
import { computeGlobalPingStats, mergeServersWithPingResults } from '@/hooks/dashboard/pingMath'
import type { ApiError } from '@/lib/api/http'

/**
 * Composition for the Ping page: only what it actually needs (no backups/rdp/zabbix/logs — we don't drag Overview in here).
 * Audit step 11.3: servers/ping loading+error used to be discarded entirely.
 */
export function usePingPageViewModel() {
  const serversQuery = useServers()
  const pingQuery = usePingStream()
  const maintenanceWindows = useMaintenanceWindows()

  const results = pingQuery.payload?.results ?? []
  const stats = computeGlobalPingStats(serversQuery.servers, results)
  const hosts = mergeServersWithPingResults(serversQuery.servers, results)

  const loading = serversQuery.loading || pingQuery.loading
  const errors = [
    serversQuery.error && { context: 'servers', error: serversQuery.error },
    pingQuery.error && { context: 'ping', error: pingQuery.error },
  ].filter((x): x is { context: string; error: ApiError } => x !== null)

  return { stats, hosts, maintenanceWindows, loading, errors }
}
