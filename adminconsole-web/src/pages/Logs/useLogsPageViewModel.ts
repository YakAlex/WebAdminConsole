import { useEffect, useState } from 'react'
import { useAppLogEntries } from '@/hooks/dashboard/useAppLogEntries'
import { LogSeverity } from '@/lib/api/types'

const LOG_PAGE_SIZE = 200
const SEARCH_DEBOUNCE_MS = 300

export function useLogsPageViewModel() {
  const [searchInput, setSearchInput] = useState('')
  const [debouncedSearch, setDebouncedSearch] = useState('')
  const [from, setFrom] = useState('')
  const [to, setTo] = useState('')

  // Крок 6 (#10): дебаунс пошуку — не бити в бекенд на кожне натискання клавіші.
  useEffect(() => {
    const id = setTimeout(() => setDebouncedSearch(searchInput.trim()), SEARCH_DEBOUNCE_MS)
    return () => clearTimeout(id)
  }, [searchInput])

  const logsQuery = useAppLogEntries(LOG_PAGE_SIZE, {
    search: debouncedSearch || undefined,
    from: from ? new Date(from).toISOString() : undefined,
    // <input type="date"> дає лише YYYY-MM-DD (північ) — +1 день, щоб обраний "to"-день був включно.
    to: to ? new Date(new Date(to).getTime() + 24 * 60 * 60 * 1000).toISOString() : undefined,
  })
  const entries = logsQuery.entries

  const infoCount = entries.filter((e) => e.severity === LogSeverity.Info).length
  const successCount = entries.filter((e) => e.severity === LogSeverity.Success).length
  const warningCount = entries.filter((e) => e.severity === LogSeverity.Warning).length
  const errorCount = entries.filter((e) => e.severity === LogSeverity.Error).length

  const clearFilters = () => {
    setSearchInput('')
    setDebouncedSearch('')
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
    // Раніше цей стан ігнорувався повністю — збій GET /api/logs виглядав
    // на екрані ідентично до "логів справді немає" (порожня таблиця, без
    // жодного повідомлення про помилку).
    fetchError: logsQuery.error,
    searchInput,
    setSearchInput,
    from,
    setFrom,
    to,
    setTo,
    hasActiveFilters: Boolean(debouncedSearch || from || to),
    clearFilters,
  }
}
