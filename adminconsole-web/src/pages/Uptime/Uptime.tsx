import { ErrorBanner } from '@/components/ui/ErrorBanner'
import { Spinner } from '@/components/ui/Spinner'
import { GlobalUptimeHealth } from '@/components/uptime/GlobalUptimeHealth/GlobalUptimeHealth'
import { UptimeDeviceTable } from '@/components/uptime/UptimeDeviceTable/UptimeDeviceTable'
import { IncidentsTable } from '@/components/uptime/IncidentsTable/IncidentsTable'
import { SlaReportSection } from '@/components/uptime/SlaReportSection/SlaReportSection'
import { useUptimePageViewModel } from './useUptimePageViewModel'
import styles from './Uptime.module.scss'

/**
 * Brief §26: Uptime does NOT copy Overview — it has its own layout for its
 * own purpose: Page header → SLA Report → 99.98% global uptime → Device table
 * (more detailed than Overview). The SLA section is deliberately at the top
 * (audit fix §3a) — report generation doesn't depend on the initialLoading
 * state of the block below it.
 * 401/403 is handled globally in App.tsx (AuthProvider) and never reaches here.
 */
export function Uptime() {
  const vm = useUptimePageViewModel()

  return (
    <div className={styles.root}>

      {vm.initialErrors.map(({ context, error }) => (
        <ErrorBanner key={context} context={context} error={error} />
      ))}

      <SlaReportSection servers={vm.servers} />

      {vm.initialLoading ? (
        <Spinner label="Loading uptime data…" />
      ) : (
        <>
          <GlobalUptimeHealth
            overallPercent={vm.overallPercent}
            trend={vm.trend}
            axisLabels={vm.axisLabels}
            monitoredDevices={vm.monitoredDevices}
            incidentsInWindow={vm.incidentsInWindow}
          />
          <UptimeDeviceTable rows={vm.deviceRows} />
          {vm.incidentActionError && <ErrorBanner context="incident update" error={vm.incidentActionError} />}
          <IncidentsTable
            incidents={vm.incidents}
            onDelete={vm.deleteIncident}
            onClearResolved={vm.clearResolvedIncidents}
            busy={vm.incidentActionBusy}
            search={vm.incidentSearch}
            onSearchChange={vm.setIncidentSearch}
            from={vm.incidentFrom}
            onFromChange={vm.setIncidentFrom}
            to={vm.incidentTo}
            onToChange={vm.setIncidentTo}
            hasActiveFilters={vm.hasActiveIncidentFilters}
            onClearFilters={vm.clearIncidentFilters}
          />
        </>
      )}
    </div>
  )
}
