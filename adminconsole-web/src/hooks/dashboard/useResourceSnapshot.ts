import { useState } from 'react'
import { useHubGroups } from '@/lib/signalr/useHubGroups'
import { useHubEvent } from '@/lib/signalr/useHubEvent'
import type { ResourceSnapshot, ResourceSnapshotUpdatedEvent } from '@/lib/api/types'

// ResourceSnapshotUpdatedOccurred маршрутизується в групу "logs"
// (SignalRBroadcastHandler — у поточному обсязі немає власної вкладки
// Resources, тож летить у загальний адміністративний потік).
const GROUPS = ['logs'] as const
const MAX_HISTORY = 20

/**
 * Бекенд шле лише ОСТАННІЙ знімок на цикл (не історію) — тому тренд для
 * sparkline накопичуємо на клієнті з реальних вхідних подій, обмежуючи
 * вікно останніми MAX_HISTORY значеннями.
 */
export function useResourceSnapshot(): ResourceSnapshot[] {
  const [history, setHistory] = useState<ResourceSnapshot[]>([])

  useHubGroups(GROUPS)
  useHubEvent<ResourceSnapshotUpdatedEvent>('ResourceSnapshotUpdatedOccurred', (evt) => {
    setHistory((prev) => [...prev, evt.snapshot].slice(-MAX_HISTORY))
  })

  return history
}
