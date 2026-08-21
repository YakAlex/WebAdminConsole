import { PageHeader } from '@/components/ui/PageHeader'
import { ErrorBanner } from '@/components/ui/ErrorBanner'
import { Spinner } from '@/components/ui/Spinner'
import { LogSeveritySummary } from '@/components/logs/LogSeveritySummary/LogSeveritySummary'
import { LogsTable } from '@/components/logs/LogsTable/LogsTable'
import { LogsFilterBar } from '@/components/logs/LogsFilterBar/LogsFilterBar'
import { useLogsPageViewModel } from './useLogsPageViewModel'
import styles from './Logs.module.scss'

/** §26 брифу: Page header → filter bar → Log severity summary → Event timeline table. */
export function Logs() {
  const vm = useLogsPageViewModel()

  return (
    <div className={styles.root}>
      <PageHeader title="Logs" subtitle="Structured application log — most recent events first." />
      {vm.fetchError && <ErrorBanner context="logs" error={vm.fetchError} />}
      {vm.loading ? (
        <Spinner label="Loading logs…" />
      ) : (
        <>
          <LogsFilterBar
            search={vm.searchInput}
            onSearchChange={vm.setSearchInput}
            onSearchSubmit={vm.submitSearch}
            searching={vm.refreshing}
            from={vm.from}
            onFromChange={vm.setFrom}
            to={vm.to}
            onToChange={vm.setTo}
            hasActiveFilters={vm.hasActiveFilters}
            onClear={vm.clearFilters}
          />
          <LogSeveritySummary info={vm.info} success={vm.success} warning={vm.warning} error={vm.error} />
          <LogsTable entries={vm.entries} />
        </>
      )}
    </div>
  )
}
