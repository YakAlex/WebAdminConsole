import { useDashboardData } from '@/hooks/useDashboardData'
import { computeUptimeAxisLabels, computeUptimeSeries } from '@/hooks/dashboard/uptimeMath'
import { computeGlobalPingStats } from '@/hooks/dashboard/pingMath'
import { BackupOutcome, RdpSessionState, ZabbixSeverity } from '@/lib/api/types'
import type { ApiError } from '@/lib/api/http'

/**
 * T6.2 §5: the single place where "raw" data from useDashboardData() is
 * transformed into ready-made props for each Overview card — replacing all
 * the mock constants that used to live directly in the components.
 */
export function useOverviewViewModel() {
  const data = useDashboardData()

  const pingResults = data.pingPayload?.results ?? []
  const ping = computeGlobalPingStats(data.servers, pingResults)

  const uptime = computeUptimeSeries(data.downtimeRecords, data.servers.length, { hours: 24, buckets: 24 })
  const uptimeAxisLabels = computeUptimeAxisLabels(24, 5)

  // Audit fix §4: disabling a service in Settings must clear ITS data
  // everywhere on Overview, rather than leaving the last known snapshot
  // ("12 problems" from Zabbix, still shown AFTER it was disabled).
  // toggles === null means it hasn't loaded yet (first render) — deliberately
  // NOT gated until the first response arrives.
  const zabbixDisabled = data.toggles?.zabbixMonitoringEnabled === false
  const rdpDisabled = data.toggles?.rdpMonitoringEnabled === false
  const backupsDisabled = data.toggles?.backupMonitoringEnabled === false

  const backupsSuccessful = backupsDisabled ? 0 : data.backups.filter((b) => b.outcome === BackupOutcome.Ok).length
  const backupsTotal = backupsDisabled ? 0 : data.backups.length

  const criticalAlerts = zabbixDisabled
    ? 0
    : data.zabbixProblems.filter((p) => p.severity === ZabbixSeverity.High || p.severity === ZabbixSeverity.Disaster).length
  const warnings = zabbixDisabled
    ? 0
    : data.zabbixProblems.filter((p) => p.severity === ZabbixSeverity.Average || p.severity === ZabbixSeverity.Warning).length

  // Audit step 11.3: Overview — the main landing page — had neither a
  // loading indicator nor an error banner, even though useDashboardData()
  // already returned *Loading/*Error for each source. A failure in any
  // REST request looked identical to "everything's fine".
  const initialLoading =
    data.serversLoading || data.pingLoading || data.downtimeLoading || data.backupsLoading ||
    data.zabbixLoading || data.rdpLoading
  const initialErrors = [
    data.serversError && { context: 'servers', error: data.serversError },
    data.pingError && { context: 'ping', error: data.pingError },
    data.downtimeError && { context: 'uptime history', error: data.downtimeError },
    data.backupsError && { context: 'backups', error: data.backupsError },
    data.zabbixError && { context: 'zabbix', error: data.zabbixError },
    data.rdpError && { context: 'rdp sessions', error: data.rdpError },
  ].filter((x): x is { context: string; error: ApiError } => x !== null)

  return {
    initialLoading,
    initialErrors,
    toggles: data.toggles,
    ping,
    uptime: {
      overallPercent: uptime.percent,
      trend: uptime.buckets,
      axisLabels: uptimeAxisLabels,
      monitoredDevices: data.servers.length,
    },
    attention: { criticalAlerts, warnings },
    backups: backupsDisabled ? [] : data.backups,
    backupsSummary: { successful: backupsSuccessful, total: backupsTotal },
    recentActivity: data.logEntries,
    // Step 3 (#4): the Overview card counts/shows only Active — data.rdpSessions
    // also includes Disconnected (the same flat list used on the full RDP
    // Sessions page), otherwise "Active sessions" would also count disconnected ones.
    rdpSessions: rdpDisabled ? [] : data.rdpSessions.filter((s) => s.state === RdpSessionState.Active),
    // No active sessions — show the last disconnect instead of an empty
    // "All clear" placeholder (less informative when someone actually logged in).
    rdpLastLogout: rdpDisabled ? null : data.rdpLastLogout,
    maintenanceWindows: data.maintenanceWindows,
  }
}
