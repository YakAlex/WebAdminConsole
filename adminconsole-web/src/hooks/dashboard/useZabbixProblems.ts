import { useEffect, useState } from 'react'
import { getZabbixProblems } from '@/lib/api/endpoints'
import { ApiError, isAuthError } from '@/lib/api/http'
import { useAuth } from '@/lib/auth/AuthContext'
import { useHubGroups } from '@/lib/signalr/useHubGroups'
import { useHubEvent } from '@/lib/signalr/useHubEvent'
import type { ZabbixProblemsPayload, ZabbixProblemsUpdatedEvent } from '@/lib/api/types'

const GROUPS = ['logs'] as const

/**
 * GET /api/zabbix (initial REST snapshot, a live poll) +
 * ZabbixProblemsUpdatedOccurred (SignalR, full snapshot on every
 * polling cycle). Audit step 11.1 — previously there was no REST
 * request here at all, and the page showed zero data until the first
 * SignalR tick after load/F5 (same as usePingStream before its fix in
 * Step 3).
 *
 * Both the REST snapshot and every SignalR push carry their own
 * fetchedAt — like usePingStream (bug report, 2026-08-22), we keep
 * whichever is newer instead of unconditionally overwriting, so a
 * slow on-demand REST response that resolves after a fresher
 * background-poll SignalR push can't roll the list back to stale data.
 */
export function useZabbixProblems() {
  const [payload, setPayload] = useState<ZabbixProblemsPayload | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<ApiError | null>(null)
  const { reportDenied } = useAuth()

  const reconnectGeneration = useHubGroups(GROUPS)

  const applyIfNewer = (next: ZabbixProblemsPayload) => {
    setPayload((prev) => (prev && prev.fetchedAt > next.fetchedAt ? prev : next))
  }

  useEffect(() => {
    let cancelled = false

    getZabbixProblems()
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

  useHubEvent<ZabbixProblemsUpdatedEvent>('ZabbixProblemsUpdatedOccurred', (evt) => applyIfNewer(evt.payload))

  return { payload, loading, error }
}
