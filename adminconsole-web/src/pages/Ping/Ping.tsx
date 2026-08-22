import { ErrorBanner } from '@/components/ui/ErrorBanner'
import { Spinner } from '@/components/ui/Spinner'
import { GlobalPingHealth } from '@/components/ping/GlobalPingHealth/GlobalPingHealth'
import { PingHostsTable } from '@/components/ping/PingHostsTable/PingHostsTable'
import { usePingPageViewModel } from './usePingPageViewModel'
import styles from './Ping.module.scss'

/**
 * §26 брифу: Ping НЕ копіює Overview — власний layout під свою функцію:
 * Global ping health → Hosts table (без окремого page header — прибрано
 * за проханням користувача, 2026-08-22, щоб Global Ping Health піднявся
 * вище). 401/403 обробляється глобально в App.tsx (AuthProvider), сюди
 * не долітає.
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
