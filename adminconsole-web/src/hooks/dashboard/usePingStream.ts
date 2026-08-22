import { useEffect, useState } from 'react'
import { getPing } from '@/lib/api/endpoints'
import { ApiError, isAuthError } from '@/lib/api/http'
import { useAuth } from '@/lib/auth/AuthContext'
import { useHubGroups } from '@/lib/signalr/useHubGroups'
import { useHubEvent } from '@/lib/signalr/useHubEvent'
import type { PingBatchPayload, PingBatchResultEvent } from '@/lib/api/types'

const GROUPS = ['ping'] as const

/**
 * GET /api/ping (початковий live-знімок при монтуванні — бекенд РЕАЛЬНО
 * пінгує сервери зараз, не просто читає застарілий кеш, див. PingController)
 * + PingBatchResultOccurred (SignalR, живі оновлення що ~PingIntervalSeconds).
 *
 * Раніше цього REST-виклику не було — картки Ping/Overview лишались
 * порожніми до першого SignalR-пуша (до 30с). Порівнюємо cycleCompletedAt
 * при кожному оновленні (з REST І з SignalR), щоб повільна REST-відповідь,
 * яка прийшла ПІСЛЯ свіжішого SignalR-пуша, не відкотила дані назад.
 */
export function usePingStream() {
  const [payload, setPayload] = useState<PingBatchPayload | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<ApiError | null>(null)
  const { reportDenied } = useAuth()

  const reconnectGeneration = useHubGroups(GROUPS)

  const applyIfNewer = (next: PingBatchPayload) => {
    setPayload((prev) => (prev && prev.cycleCompletedAt > next.cycleCompletedAt ? prev : next))
  }

  useEffect(() => {
    let cancelled = false

    getPing()
      .then((data) => {
        if (!cancelled) applyIfNewer(data)
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

  useHubEvent<PingBatchResultEvent>('PingBatchResultOccurred', (evt) => applyIfNewer(evt.payload))

  return { payload, loading, error }
}
