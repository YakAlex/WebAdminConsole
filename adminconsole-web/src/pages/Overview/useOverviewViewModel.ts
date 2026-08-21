import { useDashboardData } from '@/hooks/useDashboardData'
import { computeUptimeAxisLabels, computeUptimeSeries } from '@/hooks/dashboard/uptimeMath'
import { computeGlobalPingStats } from '@/hooks/dashboard/pingMath'
import { BackupOutcome, PingStatus, RdpSessionState, ZabbixSeverity } from '@/lib/api/types'
import type { DeviceRow } from '@/components/overview/UptimeByDeviceTable/UptimeByDeviceTable'

/**
 * T6.2 п.5: одна точка, де "сирі" дані з useDashboardData() перетворюються
 * на готові пропси для кожної картки Overview — заміна всіх mock-констант,
 * що раніше жили прямо в компонентах.
 */
export function useOverviewViewModel() {
  const data = useDashboardData()

  const pingResults = data.pingPayload?.results ?? []
  const ping = computeGlobalPingStats(data.servers, pingResults)

  const uptime = computeUptimeSeries(data.downtimeRecords, data.servers.length, { hours: 24, buckets: 12 })
  const uptimeAxisLabels = computeUptimeAxisLabels(24, 5)

  const backupsSuccessful = data.backups.filter((b) => b.outcome === BackupOutcome.Ok).length
  const backupsTotal = data.backups.length

  const criticalAlerts = data.zabbixProblems.filter(
    (p) => p.severity === ZabbixSeverity.High || p.severity === ZabbixSeverity.Disaster,
  ).length
  const warnings = data.zabbixProblems.filter(
    (p) => p.severity === ZabbixSeverity.Average || p.severity === ZabbixSeverity.Warning,
  ).length

  const deviceRows: DeviceRow[] = data.servers.map((server) => {
    const hostPing = pingResults.find((r) => r.ip === server.ip)
    const series = computeUptimeSeries(data.downtimeRecords, 1, { hours: 24, buckets: 8, serverIp: server.ip })

    return {
      ip: server.ip,
      name: server.name,
      group: server.group,
      status: hostPing?.status ?? PingStatus.Unknown,
      uptimePercent: series.percent,
      trend: series.buckets,
      responseTimeMs: hostPing?.latencyMs ?? null,
      lastCheck: hostPing?.lastChecked ?? null,
    }
  })

  return {
    ping,
    uptime: {
      overallPercent: uptime.percent,
      trend: uptime.buckets,
      axisLabels: uptimeAxisLabels,
      monitoredDevices: data.servers.length,
    },
    attention: { criticalAlerts, warnings },
    backups: data.backups,
    backupsSummary: { successful: backupsSuccessful, total: backupsTotal },
    recentActivity: data.logEntries,
    // Крок 3 (#4): картка Overview рахує/показує тільки Active — data.rdpSessions
    // включає й Disconnected (той самий плаский список, що йде на повну сторінку
    // RDP Sessions), інакше "Active sessions" рахував і відключені сесії.
    rdpSessions: data.rdpSessions.filter((s) => s.state === RdpSessionState.Active),
    maintenanceWindows: data.maintenanceWindows,
    resourceHistory: data.resourceHistory,
    deviceRows,
  }
}
