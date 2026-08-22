import { useState } from 'react'
import { useAppLogEntries } from '@/hooks/dashboard/useAppLogEntries'
import { LogSeverity } from '@/lib/api/types'

const LOG_PAGE_SIZE = 200

export function useLogsPageViewModel() {
  const [searchInput, setSearchInput] = useState('')
  // `activeSearch` is what actually goes into the request. It's updated
  // ONLY via submitSearch (Enter or the "Search" button) — audit fix, §2:
  // previously any keystroke (even with debounce) would sooner or later
  // trigger a refetch that hid/remounted the search field itself.
  const [activeSearch, setActiveSearch] = useState('')
  const [from, setFrom] = useState('')
  const [to, setTo] = useState('')

  const logsQuery = useAppLogEntries(LOG_PAGE_SIZE, {
    search: activeSearch || undefined,
    from: from ? new Date(from).toISOString() : undefined,
    // <input type="date"> only gives YYYY-MM-DD (midnight) — +1 day so the selected "to" day is inclusive.
    to: to ? new Date(new Date(to).getTime() + 24 * 60 * 60 * 1000).toISOString() : undefined,
  })
  const entries = logsQuery.entries

  const infoCount = entries.filter((e) => e.severity === LogSeverity.Info).length
  const successCount = entries.filter((e) => e.severity === LogSeverity.Success).length
  const warningCount = entries.filter((e) => e.severity === LogSeverity.Warning).length
  const errorCount = entries.filter((e) => e.severity === LogSeverity.Error).length

  const submitSearch = () => setActiveSearch(searchInput.trim())

  const clearFilters = () => {
    setSearchInput('')
    setActiveSearch('')
    setFrom('')
    setTo('')
  }

  return {
    entries,
    info: infoCount,
    success: successCount,
    warning: warningCount,
    error: errorCount,
    loading: logsQuery.loading,
    refreshing: logsQuery.refreshing,
    // This state used to be ignored entirely — a failed GET /api/logs looked
    // on screen identical to "there really are no logs" (an empty table,
    // with no error message at all).
    fetchError: logsQuery.error,
    searchInput,
    setSearchInput,
    submitSearch,
    from,
    setFrom,
    to,
    setTo,
    hasActiveFilters: Boolean(activeSearch || from || to),
    clearFilters,
  }
}
