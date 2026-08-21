import { useEffect, useState } from 'react'
import { getZabbixProblems } from '@/lib/api/endpoints'
import { ApiError, isAuthError } from '@/lib/api/http'
import { useAuth } from '@/lib/auth/AuthContext'
import { useHubGroups } from '@/lib/signalr/useHubGroups'
import { useHubEvent } from '@/lib/signalr/useHubEvent'
import type { ZabbixProblemsPayload, ZabbixProblemsUpdatedEvent } from '@/lib/api/types'

const GROUPS = ['logs'] as const

/**
 * GET /api/zabbix (початковий REST-знімок, живий опит) + ZabbixProblemsUpdatedOccurred
 * (SignalR, повний знімок на кожен цикл поллінгу). Крок 11.1 аудиту — раніше
 * тут не було REST-запиту взагалі, сторінка показувала нуль даних до першого
 * SignalR-тіка після заходу/F5 (як і usePingStream до свого фіксу в Кроці 3).
 */
export function useZabbixProblems() {
  const [payload, setPayload] = useState<ZabbixProblemsPayload | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<ApiError | null>(null)
  const { reportDenied } = useAuth()

  useHubGroups(GROUPS)

  useEffect(() => {
    let cancelled = false

    getZabbixProblems()
      .then((data) => {
        if (!cancelled) setPayload(data)
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
  }, [reportDenied])

  useHubEvent<ZabbixProblemsUpdatedEvent>('ZabbixProblemsUpdatedOccurred', (evt) => setPayload(evt.payload))

  return { payload, loading, error }
}
