import { useEffect, useState } from 'react'
import { getPing } from '@/lib/api/endpoints'
import { ApiError, isAuthError } from '@/lib/api/http'
import { useAuth } from '@/lib/auth/AuthContext'
import { useHubGroups } from '@/lib/signalr/useHubGroups'
import { useHubEvent } from '@/lib/signalr/useHubEvent'
import type { PingBatchPayload, PingBatchResultEvent } from '@/lib/api/types'

const GROUPS = ['ping'] as const

/**
 * GET /api/ping (initial live snapshot on mount — the backend ACTUALLY
 * pings the servers right now, not just reading a stale cache, see
 * PingController) + PingBatchResultOccurred (SignalR, live updates
 * roughly every PingIntervalSeconds).
 *
 * Previously this REST call didn't exist — the Ping/Overview cards
 * stayed empty until the first SignalR push (up to 30s). We compare
 * cycleCompletedAt on every update (from REST AND SignalR) so a slow
 * REST response that arrives AFTER a fresher SignalR push doesn't roll
 * the data back.
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
