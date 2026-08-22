import { useEffect, useState } from 'react'
import { getLogs, type LogQuery } from '@/lib/api/endpoints'
import { ApiError, isAuthError } from '@/lib/api/http'
import { useAuth } from '@/lib/auth/AuthContext'
import { useHubGroups } from '@/lib/signalr/useHubGroups'
import { useHubEvent } from '@/lib/signalr/useHubEvent'
import type { AppLogEntry, AppLogEntryEvent } from '@/lib/api/types'

const GROUPS = ['logs'] as const

/**
 * GET /api/logs?take=N&amp;after&amp;before&amp;search (initial page/search) +
 * AppLogEntryOccurred (new entries prepended, trimmed to N).
 *
 * Step 6 (#10): when a search/date range is active, the live stream is
 * NOT merged into the results — otherwise a freshly arrived event
 * might not match the filter but would still show up at the top of
 * the list. In "unfiltered" mode (the typical Logs view), behavior is
 * unchanged.
 */
export function useAppLogEntries(take = 20, query: LogQuery = {}) {
  const [entries, setEntries] = useState<AppLogEntry[]>([])
  // `loading` — only the FIRST page load (full-page Spinner gate).
  // `refreshing` — every subsequent refetch (search/date change).
  // Deliberately split apart (audit, item 2): previously `loading` was
  // set to true on EVERY refetch, which made the page hide (unmount)
  // its content, including the search field — the cursor/focus was
  // lost every time a search fired.
  const [loading, setLoading] = useState(true)
  const [refreshing, setRefreshing] = useState(false)
  const [error, setError] = useState<ApiError | null>(null)
  const { reportDenied } = useAuth()
  const isFiltered = Boolean(query.search || query.from || query.to)

  const reconnectGeneration = useHubGroups(GROUPS)

  useEffect(() => {
    let cancelled = false
    setRefreshing(true)

    getLogs(take, query)
      .then((data) => {
        if (!cancelled) setEntries(data)
      })
      .catch((err: unknown) => {
        if (cancelled) return
        const apiError = err instanceof ApiError ? err : new ApiError(0, 'Unknown error')
        setError(apiError)
        if (isAuthError(apiError)) reportDenied()
      })
      .finally(() => {
        if (cancelled) return
        setLoading(false)
        setRefreshing(false)
      })

    return () => {
      cancelled = true
    }
  }, [take, query.search, query.from, query.to, reportDenied, reconnectGeneration])

  useHubEvent<AppLogEntryEvent>('AppLogEntryOccurred', (evt) => {
    if (isFiltered) return
    setEntries((prev) => [evt.entry, ...prev].slice(0, take))
  })

  return { entries, loading, refreshing, error }
}
