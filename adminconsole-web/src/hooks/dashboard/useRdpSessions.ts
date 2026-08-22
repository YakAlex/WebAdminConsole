import { useEffect, useMemo, useState } from 'react'
import { getRdpSessions } from '@/lib/api/endpoints'
import { ApiError, isAuthError } from '@/lib/api/http'
import { useAuth } from '@/lib/auth/AuthContext'
import { useHubGroups } from '@/lib/signalr/useHubGroups'
import { useHubEvent } from '@/lib/signalr/useHubEvent'
import type { RdpSessionInfo, RdpSessionsPayload, RdpSessionsUpdatedEvent, RdpSnapshotPayload } from '@/lib/api/types'

const GROUPS = ['logs'] as const

export interface LastLogout {
  username: string
  serverName: string
  at: string
}

export interface RdpSessionsData {
  sessions: RdpSessionInfo[]
  dailyPeak: number
  lastLogout: LastLogout | null
  loading: boolean
  error: ApiError | null
}

/**
 * RdpSessionsUpdatedOccurred несе ПОВНИЙ список сесій ОДНОГО сервера за
 * раз (не глобальний знімок) — клієнт тримає мапу serverIp → payload і на
 * кожній події замінює лише слайс цього сервера.
 *
 * Крок 11.2 аудиту: раніше тут не було жодного REST-запиту — сторінка
 * показувала "0 сесій" до першого SignalR-тіка після заходу/F5, невідрізнимо
 * від "сесій справді нема". GET /api/rdp-sessions тепер дає живий знімок
 * одразу; поки не прийшла хоч одна SignalR-подія (byServer порожній),
 * показуємо REST-знімок як seed — щойно прилетить перша подія, переходимо
 * на live per-server модель (вона точніша на довгій дистанції).
 */
export function useRdpSessions(): RdpSessionsData {
  const [byServer, setByServer] = useState<Record<string, RdpSessionsPayload>>({})
  const [restSeed, setRestSeed] = useState<RdpSnapshotPayload | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<ApiError | null>(null)
  const { reportDenied } = useAuth()

  const reconnectGeneration = useHubGroups(GROUPS)

  useEffect(() => {
    let cancelled = false

    getRdpSessions()
      .then((data) => {
        if (!cancelled) setRestSeed(data)
      })
      .catch((err: unknown) => {
        if (cancelled) return
        const apiError = err instanceof ApiError ? err : new ApiError(0, 'Unknown error')
        setError(apiError)
        if (isAuthError(apiError)) reportDenied()
      })
      .finally(() => {
        if (!cancelled) setLoading(false)
      })

    return () => {
      cancelled = true
    }
  }, [reportDenied, reconnectGeneration])

  useHubEvent<RdpSessionsUpdatedEvent>('RdpSessionsUpdatedOccurred', (evt) => {
    setByServer((prev) => ({ ...prev, [evt.payload.serverIp]: evt.payload }))
  })

  return useMemo(() => {
    const payloads = Object.values(byServer)

    if (payloads.length === 0) {
      return {
        sessions: restSeed?.sessions ?? [],
        dailyPeak: restSeed?.globalDailyPeak ?? 0,
        lastLogout:
          restSeed?.lastLogoutAt && restSeed.lastLogoutUsername && restSeed.lastLogoutServer
            ? { username: restSeed.lastLogoutUsername, serverName: restSeed.lastLogoutServer, at: restSeed.lastLogoutAt }
            : null,
        loading,
        error,
      }
    }

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

    return { sessions, dailyPeak, lastLogout, loading, error }
  }, [byServer, restSeed, loading, error])
}
