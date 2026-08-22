import { useEffect, useState } from 'react'
import { getMonitoringToggles } from '@/lib/api/endpoints'
import { useHubGroups } from '@/lib/signalr/useHubGroups'
import { useHubEvent } from '@/lib/signalr/useHubEvent'
import { MonitoredService, type MonitoringToggledEvent, type MonitoringToggles } from '@/lib/api/types'

const GROUPS = ['logs'] as const

/**
 * Audit fix item 4: no page checked the monitoring toggle state from
 * Settings — disabling a service (e.g. Zabbix) didn't hide already
 * loaded/stale data elsewhere in the app (Overview kept showing the
 * last known "12 problems"). REST snapshot (the existing
 * GET /api/monitoring/toggles) + a live update via
 * MonitoringToggledOccurred (MonitoringController already published
 * this event on every Save — nobody on the frontend was listening to
 * it yet).
 *
 * `null` means the toggles haven't loaded yet; we deliberately gate
 * NOTHING until the first response, to avoid flashing "disabled" on
 * every F5.
 */
export function useMonitoringToggles(): MonitoringToggles | null {
  const [toggles, setToggles] = useState<MonitoringToggles | null>(null)

  const reconnectGeneration = useHubGroups(GROUPS)

  useEffect(() => {
    let cancelled = false
    getMonitoringToggles()
      .then((data) => {
        if (!cancelled) setToggles(data)
      })
      .catch(() => {
        // Toggles are non-critical page data: if this request fails,
        // we simply gate nothing (stays null) — the page's main error
        // will still surface via the corresponding *Data hook's
        // fetchError.
      })
    return () => {
      cancelled = true
    }
  }, [reconnectGeneration])

  useHubEvent<MonitoringToggledEvent>('MonitoringToggledOccurred', (evt) => {
    setToggles((prev) => {
      const base: MonitoringToggles = prev ?? {
        rdpMonitoringEnabled: true,
        zabbixMonitoringEnabled: true,
        backupMonitoringEnabled: true,
      }
      switch (evt.service) {
        case MonitoredService.Rdp:
          return { ...base, rdpMonitoringEnabled: evt.enabled }
        case MonitoredService.Zabbix:
          return { ...base, zabbixMonitoringEnabled: evt.enabled }
        case MonitoredService.Backups:
          return { ...base, backupMonitoringEnabled: evt.enabled }
        default:
          return base
      }
    })
  })

  return toggles
}
