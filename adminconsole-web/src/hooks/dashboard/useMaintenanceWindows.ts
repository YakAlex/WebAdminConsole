import { useEffect, useState } from 'react'
import { getMaintenanceWindows } from '@/lib/api/endpoints'
import { useHubGroups } from '@/lib/signalr/useHubGroups'
import { useHubEvent } from '@/lib/signalr/useHubEvent'
import { MaintenanceAction, type MaintenanceChangedEvent, type MaintenanceWindow } from '@/lib/api/types'

// MaintenanceChangedOccurred is broadcast to both "ping" and "uptime" (SignalRBroadcastHandler) — joining either one is enough.
const GROUPS = ['ping'] as const

function windowKey(window: MaintenanceWindow): string {
  return window.targetGroup ? `group:${window.targetGroup}` : (window.serverIp ?? window.displayName)
}

/**
 * Audit fix (2026-08-22, item 1): previously there was no REST snapshot
 * of active maintenance windows at all — only an event on each
 * Start/End, so windows created BEFORE the page was opened stayed
 * invisible until the next live event. GET /api/maintenance now seeds
 * the initial state (the same REST+SignalR pattern already used by
 * Zabbix/RDP/Ping), and SignalR keeps it fresh from there.
 */
export function useMaintenanceWindows(): MaintenanceWindow[] {
  const [windows, setWindows] = useState<Record<string, MaintenanceWindow>>({})

  const reconnectGeneration = useHubGroups(GROUPS)

  useEffect(() => {
    let cancelled = false
    getMaintenanceWindows()
      .then((data) => {
        if (cancelled) return
        setWindows(Object.fromEntries(data.map((w) => [windowKey(w), w])))
      })
      .catch(() => {
        // Non-critical page data — if the request fails, we just stay
        // in the empty state; SignalR will catch up on the next event.
      })
    return () => {
      cancelled = true
    }
  }, [reconnectGeneration])

  useHubEvent<MaintenanceChangedEvent>('MaintenanceChangedOccurred', (evt) => {
    setWindows((prev) => {
      const key = windowKey(evt.window)

      if (evt.action === MaintenanceAction.Ended) {
        const next = { ...prev }
        delete next[key]
        return next
      }

      return { ...prev, [key]: evt.window }
    })
  })

  return Object.values(windows)
}
