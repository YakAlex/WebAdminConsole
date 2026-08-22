import { useDashboardData } from '@/hooks/useDashboardData'
import { computeUptimeAxisLabels, computeUptimeSeries } from '@/hooks/dashboard/uptimeMath'
import { computeGlobalPingStats } from '@/hooks/dashboard/pingMath'
import { BackupOutcome, RdpSessionState, ZabbixSeverity } from '@/lib/api/types'
import type { ApiError } from '@/lib/api/http'

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

  // Аудит-фікс п.4: вимкнення сервісу в Settings мусить очищати ЙОГО дані
  // всюди на Overview, а не лишати останній відомий знімок ("12 problems"
  // від Zabbix, показаних уже ПІСЛЯ вимкнення). toggles === null — ще не
  // завантажились (перший рендер) — свідомо НЕ гейтимо до першої відповіді.
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

  // Крок 11.3 аудиту: Overview — головна сторінка входу — не мала жодного
  // loading-індикатора чи error-банера, попри те що useDashboardData() уже
  // повертав *Loading/*Error для кожного джерела. Збій будь-якого REST-запиту
  // виглядав ідентично "усе тихо і спокійно".
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
    // Крок 3 (#4): картка Overview рахує/показує тільки Active — data.rdpSessions
    // включає й Disconnected (той самий плаский список, що йде на повну сторінку
    // RDP Sessions), інакше "Active sessions" рахував і відключені сесії.
    rdpSessions: rdpDisabled ? [] : data.rdpSessions.filter((s) => s.state === RdpSessionState.Active),
    // Немає активних сесій — покажемо останній disconnect замість порожньої
    // плашки "All clear" (менш інформативно, коли насправді хтось заходив).
    rdpLastLogout: rdpDisabled ? null : data.rdpLastLogout,
    maintenanceWindows: data.maintenanceWindows,
  }
}
