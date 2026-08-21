import { PageHeader } from '@/components/ui/PageHeader'
import { ErrorBanner } from '@/components/ui/ErrorBanner'
import { Spinner } from '@/components/ui/Spinner'
import { ServiceDisabledNotice } from '@/components/ui/ServiceDisabledNotice'
import { RdpSessionsSummary } from '@/components/rdp/RdpSessionsSummary/RdpSessionsSummary'
import { RdpSessionsTable } from '@/components/rdp/RdpSessionsTable/RdpSessionsTable'
import { useMonitoringToggles } from '@/hooks/dashboard/useMonitoringToggles'
import { useRdpSessionsPageViewModel } from './useRdpSessionsPageViewModel'
import styles from './RdpSessions.module.scss'

/** §26 брифу: Page header → Active sessions summary → Connected users table. */
export function RdpSessions() {
  const vm = useRdpSessionsPageViewModel()
  const toggles = useMonitoringToggles()
  const disabled = toggles?.rdpMonitoringEnabled === false

  return (
    <div className={styles.root}>
      <PageHeader title="RDP Sessions" subtitle="Active and recent remote desktop sessions across monitored hosts." />
      {disabled ? (
        <ServiceDisabledNotice service="RDP Monitor" />
      ) : (
        <>
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
        </>
      )}
    </div>
  )
}
