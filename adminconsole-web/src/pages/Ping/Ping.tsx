import { ErrorBanner } from '@/components/ui/ErrorBanner'
import { Spinner } from '@/components/ui/Spinner'
import { GlobalPingHealth } from '@/components/ping/GlobalPingHealth/GlobalPingHealth'
import { PingHostsTable } from '@/components/ping/PingHostsTable/PingHostsTable'
import { usePingPageViewModel } from './usePingPageViewModel'
import styles from './Ping.module.scss'

/**
 * Brief §26: Ping does NOT copy Overview — it has its own layout for its
 * own purpose: Global ping health → Hosts table (no separate page header —
 * removed at the user's request, 2026-08-22, so Global Ping Health moves
 * up). 401/403 is handled globally in App.tsx (AuthProvider) and never
 * reaches here.
 */
export function Ping() {
  const vm = usePingPageViewModel()

  return (
    <div className={styles.root}>
      {vm.errors.map(({ context, error }) => (
        <ErrorBanner key={context} context={context} error={error} />
      ))}
      {vm.loading ? (
        <Spinner label="Loading ping data…" />
      ) : (
        <>
          <GlobalPingHealth
            online={vm.stats.online}
            total={vm.stats.total}
            offline={vm.stats.offline}
            successRate={vm.stats.successRate}
            avgLatencyMs={vm.stats.avgLatencyMs}
            hasData={vm.stats.hasData}
          />
          <PingHostsTable hosts={vm.hosts} maintenanceWindows={vm.maintenanceWindows} />
        </>
      )}
    </div>
  )
}
