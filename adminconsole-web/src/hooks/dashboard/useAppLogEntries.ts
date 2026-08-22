import { useEffect, useState } from 'react'
import { getLogs, type LogQuery } from '@/lib/api/endpoints'
import { ApiError, isAuthError } from '@/lib/api/http'
import { useAuth } from '@/lib/auth/AuthContext'
import { useHubGroups } from '@/lib/signalr/useHubGroups'
import { useHubEvent } from '@/lib/signalr/useHubEvent'
import type { AppLogEntry, AppLogEntryEvent } from '@/lib/api/types'

const GROUPS = ['logs'] as const

/**
 * GET /api/logs?take=N&amp;after&amp;before&amp;search (початкова сторінка/пошук) +
 * AppLogEntryOccurred (нові записи зверху, з обрізанням до N).
 *
 * Крок 6 (#10): коли активний пошук/діапазон дат, live-потік НЕ домішується
 * в результати — інакше щойно прийшла подія могла б не відповідати фільтру,
 * але все одно з'явитись зверху списку. У "нефільтрованому" режимі (типовий
 * перегляд Logs) поведінка та сама, що й раніше.
 */
export function useAppLogEntries(take = 20, query: LogQuery = {}) {
  const [entries, setEntries] = useState<AppLogEntry[]>([])
  // `loading` — лише ПЕРШЕ завантаження сторінки (full-page Spinner-gate).
  // `refreshing` — кожен наступний рефетч (зміна пошуку/дат). Розділено
  // навмисно (аудит, п.2): раніше `loading` виставлявся в true на КОЖЕН
  // рефетч, через що сторінка ховала (розмонтовувала) свій вміст, включно з
  // полем пошуку — курсор/фокус втрачався щоразу, коли спрацьовував пошук.
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
