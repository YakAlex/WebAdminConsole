import { PageHeader } from '@/components/ui/PageHeader'
import { RdpSessionsSummary } from '@/components/rdp/RdpSessionsSummary/RdpSessionsSummary'
import { RdpSessionsTable } from '@/components/rdp/RdpSessionsTable/RdpSessionsTable'
import { useRdpSessionsPageViewModel } from './useRdpSessionsPageViewModel'
import styles from './RdpSessions.module.scss'

/** §26 брифу: Page header → Active sessions summary → Connected users table. */
export function RdpSessions() {
  const vm = useRdpSessionsPageViewModel()

  return (
    <div className={styles.root}>
      <PageHeader title="RDP Sessions" subtitle="Active and recent remote desktop sessions across monitored hosts." />
      <RdpSessionsSummary
        activeCount={vm.activeCount}
        uniqueUsers={vm.uniqueUsers}
        dailyPeak={vm.dailyPeak}
        lastLogout={vm.lastLogout}
      />
      <RdpSessionsTable sessions={vm.sessions} />
    </div>
  )
}
