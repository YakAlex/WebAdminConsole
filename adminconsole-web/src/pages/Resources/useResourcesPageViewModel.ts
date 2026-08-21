import { useServers } from '@/hooks/dashboard/useServers'
import { usePingStream } from '@/hooks/dashboard/usePingStream'
import { useResourceSnapshot } from '@/hooks/dashboard/useResourceSnapshot'
import { PingStatus, ServerType } from '@/lib/api/types'
import type { ServerResourceRow } from '@/components/resources/ServersResourceTable/ServersResourceTable'

/**
 * Крок 1 UX-polish (2026-08-21, #9): сторінка Resources показує лише
 * Windows-сервери — той самий принцип, що й у WPF (ResourceMonitorViewModel
 * фільтрував .Where(s => s.Type == ServerType.Windows)), бо Linux/Network
 * тут не мають сенсу (CPU/RAM-моніторинг зав'язаний на Windows remote mgmt).
 */
export function useResourcesPageViewModel() {
  const serversQuery = useServers()
  const pingQuery = usePingStream()
  const history = useResourceSnapshot()

  const pingResults = pingQuery.payload?.results ?? []
  const windowsServers = serversQuery.servers.filter((server) => server.type === ServerType.Windows)

  const rows: ServerResourceRow[] = windowsServers.map((server) => ({
    ip: server.ip,
    name: server.name,
    group: server.group,
    type: server.type,
    status: pingResults.find((r) => r.ip === server.ip)?.status ?? PingStatus.Unknown,
  }))

  return { history, rows }
}
