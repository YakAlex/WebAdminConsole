import { PageHeader } from '@/components/ui/PageHeader'
import { ErrorBanner } from '@/components/ui/ErrorBanner'
import { Spinner } from '@/components/ui/Spinner'
import { ResourcesOverview } from '@/components/resources/ResourcesOverview/ResourcesOverview'
import { ServersResourceTable } from '@/components/resources/ServersResourceTable/ServersResourceTable'
import { useResourcesPageViewModel } from './useResourcesPageViewModel'
import styles from './Resources.module.scss'

/** §26 брифу: Page header → CPU/RAM overview → Servers table. */
export function Resources() {
  const vm = useResourcesPageViewModel()

  return (
    <div className={styles.root}>
      <PageHeader title="Resources" subtitle="CPU and memory utilization for the local host and monitored servers." />
      {vm.errors.map(({ context, error }) => (
        <ErrorBanner key={context} context={context} error={error} />
      ))}
      {vm.loading ? (
        <Spinner label="Loading resources…" />
      ) : (
        <>
          <ResourcesOverview history={vm.history} />
          <ServersResourceTable rows={vm.rows} />
        </>
      )}
    </div>
  )
}
