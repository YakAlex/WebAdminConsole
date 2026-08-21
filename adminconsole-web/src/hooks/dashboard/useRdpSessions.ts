import { useMemo, useState } from 'react'
import { useHubGroups } from '@/lib/signalr/useHubGroups'
import { useHubEvent } from '@/lib/signalr/useHubEvent'
import type { RdpSessionInfo, RdpSessionsPayload, RdpSessionsUpdatedEvent } from '@/lib/api/types'

const GROUPS = ['logs'] as const

export interface LastLogout {
  username: string
  serverName: string
  at: string
}

export interface RdpSessionsData {
  sessions: RdpSessionInfo[]
  /** Пік одночасних сесій за сьогодні — поле вже глобальне на бекенді (RdpSessionsPayload.globalDailyPeak), беремо максимум з отриманих подій. */
  dailyPeak: number
  /** Останній логаут по всій інфраструктурі — найсвіжіший LastLogoutAt серед подій. */
  lastLogout: LastLogout | null
}

/**
 * RdpSessionsUpdatedOccurred несе ПОВНИЙ список сесій ОДНОГО сервера за
 * раз (не глобальний знімок) — тому клієнт тримає мапу serverIp → payload
 * і на кожній події замінює лише слайс цього сервера, віддаючи назовні
 * плаский список усіх активних сесій по всій інфраструктурі + похідну
 * агрегатну статистику (пік/останній logout — реальні поля з бекенду).
 */
export function useRdpSessions(): RdpSessionsData {
  const [byServer, setByServer] = useState<Record<string, RdpSessionsPayload>>({})

  useHubGroups(GROUPS)
  useHubEvent<RdpSessionsUpdatedEvent>('RdpSessionsUpdatedOccurred', (evt) => {
    setByServer((prev) => ({ ...prev, [evt.payload.serverIp]: evt.payload }))
  })

  return useMemo(() => {
    const payloads = Object.values(byServer)
    const sessions = payloads.flatMap((payload) => payload.sessions)
    const dailyPeak = payloads.reduce((max, p) => Math.max(max, p.globalDailyPeak), 0)

    const lastLogout = payloads
      .filter((p) => p.lastLogoutAt != null)
      .reduce<LastLogout | null>((latest, p) => {
        if (!p.lastLogoutAt || !p.lastLogoutUsername || !p.lastLogoutServer) return latest
        if (!latest || new Date(p.lastLogoutAt) > new Date(latest.at)) {
          return { username: p.lastLogoutUsername, serverName: p.lastLogoutServer, at: p.lastLogoutAt }
        }
        return latest
      }, null)

    return { sessions, dailyPeak, lastLogout }
  }, [byServer])
}
