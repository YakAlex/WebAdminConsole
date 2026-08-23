import { ErrorBanner } from '@/components/ui/ErrorBanner'
import { Spinner } from '@/components/ui/Spinner'
import { SystemHealthHero } from '@/components/overview/SystemHealthHero/SystemHealthHero'
import { AttentionRequired } from '@/components/overview/AttentionRequired/AttentionRequired'
import { PingCard } from '@/components/overview/PingCard/PingCard'
import { UptimeCard } from '@/components/overview/UptimeCard/UptimeCard'
import { RecentActivity } from '@/components/overview/RecentActivity/RecentActivity'
import { BackupsCard } from '@/components/overview/BackupsCard/BackupsCard'
import { RdpSessionsCard } from '@/components/overview/RdpSessionsCard/RdpSessionsCard'
import { MaintenanceCard } from '@/components/overview/MaintenanceCard/MaintenanceCard'
import { useOverviewViewModel } from './useOverviewViewModel'
import styles from './Overview.module.scss'

/**
 * Brief §6–17: reference implementation of the Design System. T6.2: the data
 * is now real — an initial REST snapshot plus the SignalR DashboardHub,
 * composed in useOverviewViewModel(). 401/403 is handled globally in App.tsx
 * (AuthProvider) — this component only mounts once access has been confirmed.
 */
export function Overview() {
  const vm = useOverviewViewModel()

  return (
    <div className={styles.root}>
      {vm.initialErrors.map(({ context, error }) => (
        <ErrorBanner key={context} context={context} error={error} />
      ))}

      {vm.initialLoading ? (
        <Spinner label="Loading dashboard…" />
      ) : (
        <>
          <div className={styles.heroRow}>
            <SystemHealthHero
              online={vm.ping.online}
              total={vm.ping.total}
              hasPingData={vm.ping.hasData}
              pingSuccessRate={vm.ping.successRate}
              uptimePercent={vm.uptime.overallPercent}
              backupsSuccessful={vm.backupsSummary.successful}
              backupsTotal={vm.backupsSummary.total}
              criticalAlerts={vm.attention.criticalAlerts}
              warnings={vm.attention.warnings}
              info={vm.attention.info}
            />
            <AttentionRequired
              criticalAlerts={vm.attention.criticalAlerts}
              warnings={vm.attention.warnings}
              info={vm.attention.info}
              disabled={vm.toggles?.zabbixMonitoringEnabled === false}
            />
          </div>

          <div className={styles.grid}>
            <div className={styles.ping}>
              <PingCard
                online={vm.ping.online}
                total={vm.ping.total}
                successRate={vm.ping.successRate}
                offline={vm.ping.offline}
                hasData={vm.ping.hasData}
              />
            </div>
            <div className={styles.uptime}>
              <UptimeCard
                overallPercent={vm.uptime.overallPercent}
                monitoredDevices={vm.uptime.monitoredDevices}
                trend={vm.uptime.trend}
                axisLabels={vm.uptime.axisLabels}
              />
            </div>
            <div className={styles.activity}>
              <RecentActivity entries={vm.recentActivity} />
            </div>
            <div className={styles.backups}>
              <BackupsCard jobs={vm.backups} disabled={vm.toggles?.backupMonitoringEnabled === false} />
            </div>
            <div className={styles.rdp}>
              <RdpSessionsCard
                sessions={vm.rdpSessions}
                lastLogout={vm.rdpLastLogout}
                disabled={vm.toggles?.rdpMonitoringEnabled === false}
              />
            </div>
            <div className={styles.maintenance}>
              <MaintenanceCard windows={vm.maintenanceWindows} />
            </div>
          </div>
        </>
      )}
    </div>
  )
}
