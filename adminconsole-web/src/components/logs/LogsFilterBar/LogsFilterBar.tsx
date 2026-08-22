import { Search, X } from 'lucide-react'
import styles from './LogsFilterBar.module.scss'

export interface LogsFilterBarProps {
  search: string
  onSearchChange: (value: string) => void
  onSearchSubmit: () => void
  searching?: boolean
  from: string
  onFromChange: (value: string) => void
  to: string
  onToChange: (value: string) => void
  hasActiveFilters: boolean
  onClear: () => void
}

/**
 * Step 6 (#10) + audit fix item 2: search by Source/Message is now
 * triggered ONLY by Enter/the "Search" button (not on every keystroke) —
 * typing in the field no longer causes a refetch and, as a result, no
 * longer loses cursor focus.
 */
export function LogsFilterBar({
  search,
  onSearchChange,
  onSearchSubmit,
  searching,
  from,
  onFromChange,
  to,
  onToChange,
  hasActiveFilters,
  onClear,
}: LogsFilterBarProps) {
  return (
    <div className={styles.bar}>
      <form
        className={styles.searchField}
        onSubmit={(e) => {
          e.preventDefault()
          onSearchSubmit()
        }}
      >
        <Search size={14} strokeWidth={1.75} className={styles.searchIcon} />
        <input
          type="text"
          className={styles.searchInput}
          placeholder="Search source or message…"
          value={search}
          onChange={(e) => onSearchChange(e.target.value)}
        />
        <button type="submit" className={styles.searchButton} disabled={searching}>
          {searching ? 'Searching…' : 'Search'}
        </button>
      </form>

      <div className={styles.dateField}>
        <label className={styles.dateLabel}>
          From
          <input type="date" className={styles.dateInput} value={from} onChange={(e) => onFromChange(e.target.value)} />
        </label>
        <label className={styles.dateLabel}>
          To
          <input type="date" className={styles.dateInput} value={to} onChange={(e) => onToChange(e.target.value)} />
        </label>
      </div>

      {hasActiveFilters && (
        <button type="button" className={styles.clearButton} onClick={onClear}>
          <X size={13} strokeWidth={2} />
          Clear filters
        </button>
      )}
    </div>
  )
}
