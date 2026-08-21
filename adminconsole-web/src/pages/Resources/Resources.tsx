import { PageHeader } from '@/components/ui/PageHeader'
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
      <ResourcesOverview history={vm.history} />
      <ServersResourceTable rows={vm.rows} />
    </div>
  )
}
