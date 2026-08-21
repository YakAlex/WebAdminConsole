import { Search, X } from 'lucide-react'
import styles from './LogsFilterBar.module.scss'

export interface LogsFilterBarProps {
  search: string
  onSearchChange: (value: string) => void
  from: string
  onFromChange: (value: string) => void
  to: string
  onToChange: (value: string) => void
  hasActiveFilters: boolean
  onClear: () => void
}

/** Крок 6 (#10): пошук по Source/Message + діапазон дат для Logs. */
export function LogsFilterBar({
  search,
  onSearchChange,
  from,
  onFromChange,
  to,
  onToChange,
  hasActiveFilters,
  onClear,
}: LogsFilterBarProps) {
  return (
    <div className={styles.bar}>
      <div className={styles.searchField}>
        <Search size={14} strokeWidth={1.75} className={styles.searchIcon} />
        <input
          type="text"
          className={styles.searchInput}
          placeholder="Search source or message…"
          value={search}
          onChange={(e) => onSearchChange(e.target.value)}
        />
      </div>

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
