import { useServers } from '@/hooks/dashboard/useServers'
import { usePingStream } from '@/hooks/dashboard/usePingStream'
import { computeGlobalPingStats, mergeServersWithPingResults } from '@/hooks/dashboard/pingMath'

/** Композиція для сторінки Ping: лише те, що їй реально треба (без backups/rdp/zabbix/logs — Overview туди не тягнемо). */
export function usePingPageViewModel() {
  const serversQuery = useServers()
  const pingQuery = usePingStream()

  const results = pingQuery.payload?.results ?? []
  const stats = computeGlobalPingStats(serversQuery.servers, results)
  const hosts = mergeServersWithPingResults(serversQuery.servers, results)

  return { stats, hosts }
}
