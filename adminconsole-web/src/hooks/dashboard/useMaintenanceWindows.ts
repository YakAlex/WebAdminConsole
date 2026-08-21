import { useState } from 'react'
import { useHubGroups } from '@/lib/signalr/useHubGroups'
import { useHubEvent } from '@/lib/signalr/useHubEvent'
import { MaintenanceAction, type MaintenanceChangedEvent, type MaintenanceWindow } from '@/lib/api/types'

// MaintenanceChangedOccurred летить і в "ping", і в "uptime" (SignalRBroadcastHandler) — досить приєднатись до однієї.
const GROUPS = ['ping'] as const

function windowKey(window: MaintenanceWindow): string {
  return window.targetGroup ? `group:${window.targetGroup}` : (window.serverIp ?? window.displayName)
}

/**
 * Немає REST-знімка активних вікон обслуговування — лише подія на кожен
 * Start/End. Тому клієнт сам тримає активний набір: Started додає/оновлює
 * запис за ключем, Ended прибирає його. Порожньо, доки не прийде перша
 * подія відколи відкрита сторінка — чесний "порожній стан", а не мок.
 */
export function useMaintenanceWindows(): MaintenanceWindow[] {
  const [windows, setWindows] = useState<Record<string, MaintenanceWindow>>({})

  useHubGroups(GROUPS)
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
