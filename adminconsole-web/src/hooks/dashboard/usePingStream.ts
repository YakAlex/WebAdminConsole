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
 *
 * PingMonitorService's recovery loop (PingMonitorService.RunRecoveryLoopAsync)
 * re-pings ONLY the currently-offline servers and publishes a
 * PingBatchResultOccurred containing JUST that subset, on a shorter
 * interval than the main loop. Its cycleCompletedAt is always newer than
 * the main loop's last full batch, so naively replacing the whole payload
 * (by cycleCompletedAt alone) collapsed the fleet-wide view down to only
 * the offline hosts every recovery cycle — global stats briefly read
 * "0/2 online" and every other host's row flipped to Unknown, until the
 * next full main-loop batch arrived and overwrote it back (bug report,
 * 2026-08-22). We merge results by host IP instead, keeping each host's
 * own latest entry (by its own lastChecked) rather than replacing the
 * entire results array.
 */
export function usePingStream() {
  const [payload, setPayload] = useState<PingBatchPayload | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<ApiError | null>(null)
  const { reportDenied } = useAuth()

  const reconnectGeneration = useHubGroups(GROUPS)

  const applyIfNewer = (next: PingBatchPayload) => {
    setPayload((prev) => {
      if (!prev) return next

      const byIp = new Map(prev.results.map((r) => [r.ip, r]))
      for (const result of next.results) {
        const existing = byIp.get(result.ip)
        if (!existing || existing.lastChecked <= result.lastChecked) {
          byIp.set(result.ip, result)
        }
      }

      return {
        results: Array.from(byIp.values()),
        cycleCompletedAt: prev.cycleCompletedAt > next.cycleCompletedAt ? prev.cycleCompletedAt : next.cycleCompletedAt,
      }
    })
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
