import { useDashboardConnection } from '@/lib/signalr/DashboardConnectionContext'
import { useServers } from './dashboard/useServers'
import { usePingStream } from './dashboard/usePingStream'
import { useDowntimeData } from './dashboard/useDowntimeData'
import { useBackupsData } from './dashboard/useBackupsData'
import { useRdpSessions } from './dashboard/useRdpSessions'
import { useZabbixProblems } from './dashboard/useZabbixProblems'
import { useMaintenanceWindows } from './dashboard/useMaintenanceWindows'
import { useAppLogEntries } from './dashboard/useAppLogEntries'
import { useMonitoringToggles } from './dashboard/useMonitoringToggles'

/**
 * T6.2: a single entry point for pages that need dashboard data —
 * combines the initial REST load (servers/downtime/backups/logs) with
 * live SignalR updates (ping/uptime/backups/rdp/zabbix/maintenance).
 * Each subdomain is a separate hook in hooks/dashboard/; this is just
 * the composition.
 *
 * 401/403 is no longer handled here — that's the responsibility of
 * AuthProvider (lib/auth/AuthContext.tsx), which blocks rendering the
 * whole App before these hooks even get a chance to mount (see the
 * Flash of Unauthenticated Content feedback).
 */
export function useDashboardData() {
  const serversQuery = useServers()
  const pingQuery = usePingStream()
  const downtimeQuery = useDowntimeData()
  const backupsQuery = useBackupsData()
  const rdp = useRdpSessions()
  const zabbixQuery = useZabbixProblems()
  const maintenanceWindows = useMaintenanceWindows()
  const logsQuery = useAppLogEntries(20)
  const toggles = useMonitoringToggles()
  const { state: hubState } = useDashboardConnection()

  return {
    hubState,
    toggles,
    servers: serversQuery.servers,
    serversLoading: serversQuery.loading,
    serversError: serversQuery.error,
    pingPayload: pingQuery.payload,
    pingLoading: pingQuery.loading,
    pingError: pingQuery.error,
    downtimeRecords: downtimeQuery.records,
    downtimeLoading: downtimeQuery.loading,
    downtimeError: downtimeQuery.error,
    backups: backupsQuery.states,
    backupsLoading: backupsQuery.loading,
    backupsError: backupsQuery.error,
    rdpSessions: rdp.sessions,
    rdpLastLogout: rdp.lastLogout,
    rdpLoading: rdp.loading,
    rdpError: rdp.error,
    zabbixProblems: zabbixQuery.payload?.problems ?? [],
    zabbixLoading: zabbixQuery.loading,
    zabbixError: zabbixQuery.error,
    maintenanceWindows,
    logEntries: logsQuery.entries,
  }
}

export type DashboardData = ReturnType<typeof useDashboardData>
