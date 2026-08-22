import { useEffect, useState } from 'react'
import { getDowntime } from '@/lib/api/endpoints'
import { ApiError, isAuthError } from '@/lib/api/http'
import { useAuth } from '@/lib/auth/AuthContext'
import { useHubGroups } from '@/lib/signalr/useHubGroups'
import { useHubEvent } from '@/lib/signalr/useHubEvent'
import type { DowntimeRecord, UptimeUpdatedEvent } from '@/lib/api/types'

const GROUPS = ['uptime'] as const

/** GET /api/downtime (початковий знімок) + UptimeUpdatedOccurred (повний знімок при кожній зміні). */
export function useDowntimeData() {
  const [records, setRecords] = useState<DowntimeRecord[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<ApiError | null>(null)
  const { reportDenied } = useAuth()

  const reconnectGeneration = useHubGroups(GROUPS)

  useEffect(() => {
    let cancelled = false

    getDowntime()
      .then((data) => {
        if (!cancelled) setRecords(data)
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

  useHubEvent<UptimeUpdatedEvent>('UptimeUpdatedOccurred', (evt) => setRecords(evt.snapshot))

  return { records, loading, error }
}
