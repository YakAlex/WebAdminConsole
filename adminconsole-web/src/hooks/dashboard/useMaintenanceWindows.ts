import { useEffect, useState } from 'react'
import { getMaintenanceWindows } from '@/lib/api/endpoints'
import { useHubGroups } from '@/lib/signalr/useHubGroups'
import { useHubEvent } from '@/lib/signalr/useHubEvent'
import { MaintenanceAction, type MaintenanceChangedEvent, type MaintenanceWindow } from '@/lib/api/types'

// MaintenanceChangedOccurred летить і в "ping", і в "uptime" (SignalRBroadcastHandler) — досить приєднатись до однієї.
const GROUPS = ['ping'] as const

function windowKey(window: MaintenanceWindow): string {
  return window.targetGroup ? `group:${window.targetGroup}` : (window.serverIp ?? window.displayName)
}

/**
 * Аудит-фікс (2026-08-22, п.1): раніше не було REST-знімка активних вікон
 * обслуговування взагалі — лише подія на кожен Start/End, тож вікна,
 * створені ДО того як відкрили сторінку, були невидимі аж до наступної
 * live-події. GET /api/maintenance тепер сідить початковий стан (той самий
 * REST+SignalR патерн, що вже в Zabbix/RDP/Ping), SignalR і далі тримає
 * його свіжим.
 */
export function useMaintenanceWindows(): MaintenanceWindow[] {
  const [windows, setWindows] = useState<Record<string, MaintenanceWindow>>({})

  useHubGroups(GROUPS)

  useEffect(() => {
    let cancelled = false
    getMaintenanceWindows()
      .then((data) => {
        if (cancelled) return
        setWindows(Object.fromEntries(data.map((w) => [windowKey(w), w])))
      })
      .catch(() => {
        // Не критичні дані сторінки — якщо запит впав, лишаємось на
        // порожньому стані, SignalR і так наздожене на наступній події.
      })
    return () => {
      cancelled = true
    }
  }, [])

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
