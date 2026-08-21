import { PageHeader } from '@/components/ui/PageHeader'
import { ErrorBanner } from '@/components/ui/ErrorBanner'
import { Spinner } from '@/components/ui/Spinner'
import { ServiceDisabledNotice } from '@/components/ui/ServiceDisabledNotice'
import { BackupsSummary } from '@/components/backups/BackupsSummary/BackupsSummary'
import { BackupJobsTable } from '@/components/backups/BackupJobsTable/BackupJobsTable'
import { useMonitoringToggles } from '@/hooks/dashboard/useMonitoringToggles'
import { useBackupsPageViewModel } from './useBackupsPageViewModel'
import styles from './Backups.module.scss'

/** §26 брифу: Page header → Success rate summary → Jobs table (детальна). */
export function Backups() {
  const vm = useBackupsPageViewModel()
  const toggles = useMonitoringToggles()
  const disabled = toggles?.backupMonitoringEnabled === false

  return (
    <div className={styles.root}>
      <PageHeader title="Backups" subtitle="Backup job health and execution history across all monitored servers." />
      {disabled ? (
        <ServiceDisabledNotice service="Backup Monitor" />
      ) : (
        <>
          {vm.error && <ErrorBanner context="backups" error={vm.error} />}
          {vm.loading ? (
            <Spinner label="Loading backups…" />
          ) : (
            <>
              <BackupsSummary successRate={vm.successRate} successful={vm.successful} warnings={vm.warnings} failed={vm.failed} />
              <BackupJobsTable jobs={vm.jobs} />
            </>
          )}
        </>
      )}
    </div>
  )
}
