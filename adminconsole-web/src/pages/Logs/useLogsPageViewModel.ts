import { useState } from 'react'
import { useAppLogEntries } from '@/hooks/dashboard/useAppLogEntries'
import { LogSeverity } from '@/lib/api/types'

const LOG_PAGE_SIZE = 200

export function useLogsPageViewModel() {
  const [searchInput, setSearchInput] = useState('')
  // `activeSearch` — те, що реально йде в запит. Оновлюється ЛИШЕ через
  // submitSearch (Enter або кнопка "Пошук") — аудит-фікс, п.2: раніше
  // будь-яке натискання клавіші (навіть із дебаунсом) рано чи пізно
  // тригерило рефетч, який ховав/перемонтовував саме поле пошуку.
  const [activeSearch, setActiveSearch] = useState('')
  const [from, setFrom] = useState('')
  const [to, setTo] = useState('')

  const logsQuery = useAppLogEntries(LOG_PAGE_SIZE, {
    search: activeSearch || undefined,
    from: from ? new Date(from).toISOString() : undefined,
    // <input type="date"> дає лише YYYY-MM-DD (північ) — +1 день, щоб обраний "to"-день був включно.
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
    // Раніше цей стан ігнорувався повністю — збій GET /api/logs виглядав
    // на екрані ідентично до "логів справді немає" (порожня таблиця, без
    // жодного повідомлення про помилку).
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
