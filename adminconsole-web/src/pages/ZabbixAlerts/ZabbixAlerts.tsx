import { PageHeader } from '@/components/ui/PageHeader'
import { ErrorBanner } from '@/components/ui/ErrorBanner'
import { Spinner } from '@/components/ui/Spinner'
import { ServiceDisabledNotice } from '@/components/ui/ServiceDisabledNotice'
import { ZabbixSeveritySummary } from '@/components/zabbix/ZabbixSeveritySummary/ZabbixSeveritySummary'
import { ZabbixProblemsTable } from '@/components/zabbix/ZabbixProblemsTable/ZabbixProblemsTable'
import { useMonitoringToggles } from '@/hooks/dashboard/useMonitoringToggles'
import { useZabbixAlertsPageViewModel } from './useZabbixAlertsPageViewModel'
import styles from './ZabbixAlerts.module.scss'

/** Brief §26: Page header → Critical/Warning/Informational summary → Active problems table. */
export function ZabbixAlerts() {
  const vm = useZabbixAlertsPageViewModel()
  const toggles = useMonitoringToggles()
  const disabled = toggles?.zabbixMonitoringEnabled === false

  return (
    <div className={styles.root}>
      <PageHeader title="Zabbix Alerts" subtitle="Active problems reported by Zabbix across all monitored hosts." />
      {disabled ? (
        <ServiceDisabledNotice service="Zabbix Monitor" />
      ) : (
        <>
          {vm.fetchError && <ErrorBanner context="zabbix" error={vm.fetchError} />}
          {vm.loading ? (
            <Spinner label="Loading Zabbix alerts…" />
          ) : (
            <>
              <ZabbixSeveritySummary critical={vm.critical} warning={vm.warning} info={vm.info} errorMessage={vm.errorMessage} />
              <ZabbixProblemsTable problems={vm.problems} />
            </>
          )}
        </>
      )}
    </div>
  )
}
