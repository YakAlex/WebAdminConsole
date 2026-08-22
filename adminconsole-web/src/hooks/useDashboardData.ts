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
 * T6.2: єдина точка входу для сторінок, яким потрібні дані дашборду —
 * поєднує початкове REST-завантаження (servers/downtime/backups/logs) з
 * живими SignalR-оновленнями (ping/uptime/backups/rdp/zabbix/maintenance).
 * Кожен піддомен — окремий хук у hooks/dashboard/, тут лише композиція.
 *
 * 401/403 більше не рахується тут — це відповідальність AuthProvider
 * (lib/auth/AuthContext.tsx), який блокує рендер усього App ще до того, як
 * ці хуки взагалі встигають змонтуватись (див. фідбек про Flash of
 * Unauthenticated Content).
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
