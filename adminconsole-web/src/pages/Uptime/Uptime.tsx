import { PageHeader } from '@/components/ui/PageHeader'
import { GlobalUptimeHealth } from '@/components/uptime/GlobalUptimeHealth/GlobalUptimeHealth'
import { UptimeDeviceTable } from '@/components/uptime/UptimeDeviceTable/UptimeDeviceTable'
import { IncidentsTable } from '@/components/uptime/IncidentsTable/IncidentsTable'
import { useUptimePageViewModel } from './useUptimePageViewModel'
import styles from './Uptime.module.scss'

/**
 * §26 брифу: Uptime НЕ копіює Overview — власний layout під свою функцію:
 * Page header → 99.98% global uptime → Device table (детальніша за Overview).
 * 401/403 обробляється глобально в App.tsx (AuthProvider), сюди не долітає.
 */
export function Uptime() {
  const vm = useUptimePageViewModel()

  return (
    <div className={styles.root}>
      <PageHeader title="Uptime" subtitle="Historical availability and incident tracking across all monitored devices." />
      <GlobalUptimeHealth
        overallPercent={vm.overallPercent}
        trend={vm.trend}
        axisLabels={vm.axisLabels}
        monitoredDevices={vm.monitoredDevices}
        incidentsInWindow={vm.incidentsInWindow}
      />
      <UptimeDeviceTable rows={vm.deviceRows} />
      {vm.incidentActionError && (
        <div className={styles.error}>
          Failed to update incidents: {vm.incidentActionError.message}
        </div>
      )}
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
    </div>
  )
}
