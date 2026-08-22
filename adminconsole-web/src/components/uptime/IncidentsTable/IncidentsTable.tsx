import { Search, Trash2, X } from 'lucide-react'
import clsx from 'clsx'
import { StatusDot } from '@/components/ui/StatusDot'
import { formatDateTime, formatDuration } from '@/lib/format'
import type { DowntimeRecord } from '@/lib/api/types'
import styles from './IncidentsTable.module.scss'

export interface IncidentsTableProps {
  /** Already filtered and sorted: open ones on top, closed ones newest to oldest (useUptimePageViewModel). */
  incidents: DowntimeRecord[]
  onDelete: (serverIp: string, fellAt: string) => void
  onClearResolved: () => void
  busy: boolean
  search: string
  onSearchChange: (value: string) => void
  from: string
  onFromChange: (value: string) => void
  to: string
  onToChange: (value: string) => void
  hasActiveFilters: boolean
  onClearFilters: () => void
}

/**
 * Step 5 (#3): the full list of DowntimeRecord entries (not an aggregate
 * like UptimeDeviceTable above) — counterpart to WPF
 * UptimeViewModel.RecordsView. Deletion is only available for closed
 * incidents (the server enforces this too — DowntimeController.Delete).
 * Step 6 (#10): search by server name + date range is client-side
 * (useDowntimeData already holds the full snapshot, no separate request needed).
 */
export function IncidentsTable({
  incidents,
  onDelete,
  onClearResolved,
  busy,
  search,
  onSearchChange,
  from,
  onFromChange,
  to,
  onToChange,
  hasActiveFilters,
  onClearFilters,
}: IncidentsTableProps) {
  const hasResolved = incidents.some((i) => i.recoveredAt !== null)

  return (
    <div className={styles.card}>
      <div className={styles.header}>
        <span className={styles.eyebrow}>Incidents</span>
        <button
          type="button"
          className={styles.clearButton}
          onClick={onClearResolved}
          disabled={busy || !hasResolved}
        >
          Clear resolved
        </button>
      </div>

      <div className={styles.filterRow}>
        <div className={styles.searchField}>
          <Search size={14} strokeWidth={1.75} className={styles.searchIcon} />
          <input
            type="text"
            className={styles.searchInput}
            placeholder="Search by device name…"
            value={search}
            onChange={(e) => onSearchChange(e.target.value)}
          />
        </div>
        <label className={styles.dateLabel}>
          From
          <input type="date" className={styles.dateInput} value={from} onChange={(e) => onFromChange(e.target.value)} />
        </label>
        <label className={styles.dateLabel}>
          To
          <input type="date" className={styles.dateInput} value={to} onChange={(e) => onToChange(e.target.value)} />
        </label>
        {hasActiveFilters && (
          <button type="button" className={styles.clearFiltersButton} onClick={onClearFilters}>
            <X size={13} strokeWidth={2} />
            Clear filters
          </button>
        )}
      </div>

      {incidents.length === 0 ? (
        <div className={styles.empty}>{hasActiveFilters ? 'No incidents match the filter' : 'No incidents recorded'}</div>
      ) : (
        <div className={styles.tableWrap}>
          <table className={styles.table}>
            <thead>
              <tr>
                <th>Device</th>
                <th>Group</th>
                <th>Status</th>
                <th>Fell at</th>
                <th>Recovered at</th>
                <th>Duration</th>
                <th aria-label="Actions" />
              </tr>
            </thead>
            <tbody>
              {incidents.map((incident) => {
                const isOpen = incident.recoveredAt === null
                return (
                  <tr key={`${incident.serverIp}-${incident.fellAt}`}>
                    <td className={styles.device}>{incident.serverName}</td>
                    <td className={styles.group}>{incident.serverGroup}</td>
                    <td>
                      <span className={clsx(styles.status, isOpen ? styles.statusOpen : styles.statusClosed)}>
                        <StatusDot tone={isOpen ? 'critical' : 'success'} />
                        {isOpen ? 'Open' : 'Closed'}
                      </span>
                    </td>
                    <td className={styles.time}>{formatDateTime(incident.fellAt)}</td>
                    <td className={styles.time}>
                      {incident.recoveredAt ? formatDateTime(incident.recoveredAt) : '—'}
                    </td>
                    <td className={styles.duration}>{formatDuration(incident.fellAt, incident.recoveredAt)}</td>
                    <td className={styles.actions}>
                      {!isOpen && (
                        <button
                          type="button"
                          className={styles.deleteButton}
                          onClick={() => onDelete(incident.serverIp, incident.fellAt)}
                          disabled={busy}
                          aria-label={`Delete incident for ${incident.serverName}`}
                        >
                          <Trash2 size={14} strokeWidth={1.75} />
                        </button>
                      )}
                    </td>
                  </tr>
                )
              })}
            </tbody>
          </table>
        </div>
      )}
    </div>
  )
}
