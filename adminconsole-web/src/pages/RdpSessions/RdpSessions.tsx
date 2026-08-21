import { PageHeader } from '@/components/ui/PageHeader'
import { ErrorBanner } from '@/components/ui/ErrorBanner'
import { Spinner } from '@/components/ui/Spinner'
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
      {vm.fetchError && <ErrorBanner context="rdp sessions" error={vm.fetchError} />}
      {vm.loading ? (
        <Spinner label="Loading RDP sessions…" />
      ) : (
        <>
          <RdpSessionsSummary
            activeCount={vm.activeCount}
            uniqueUsers={vm.uniqueUsers}
            dailyPeak={vm.dailyPeak}
            lastLogout={vm.lastLogout}
          />
          <RdpSessionsTable sessions={vm.sessions} />
        </>
      )}
    </div>
  )
}
