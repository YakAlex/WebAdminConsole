import { StatusDot, type StatusTone } from '@/components/ui/StatusDot'
import { LogSeverity, type AppLogEntry } from '@/lib/api/types'
import { formatDateTime } from '@/lib/format'
import styles from './LogsTable.module.scss'

const SEVERITY_LABEL: Record<LogSeverity, string> = {
  [LogSeverity.Info]: 'Info',
  [LogSeverity.Success]: 'Success',
  [LogSeverity.Warning]: 'Warning',
  [LogSeverity.Error]: 'Error',
}

const SEVERITY_TONE: Record<LogSeverity, StatusTone> = {
  [LogSeverity.Info]: 'info',
  [LogSeverity.Success]: 'success',
  [LogSeverity.Warning]: 'warning',
  [LogSeverity.Error]: 'critical',
}

const SEVERITY_TEXT_CLASS: Record<LogSeverity, string> = {
  [LogSeverity.Info]: 'severityInfo',
  [LogSeverity.Success]: 'severitySuccess',
  [LogSeverity.Warning]: 'severityWarning',
  [LogSeverity.Error]: 'severityError',
}

export interface LogsTableProps {
  entries: AppLogEntry[]
}

/** Brief §26 (Logs): "Log table" / event timeline. Data — useAppLogEntries() (GET /api/logs + AppLogEntryOccurred). */
export function LogsTable({ entries }: LogsTableProps) {
  return (
    <div className={styles.card}>
      <div className={styles.header}>
        <span className={styles.eyebrow}>Event Timeline</span>
      </div>

      {entries.length === 0 ? (
        <div className={styles.empty}>No log entries</div>
      ) : (
        <div className={styles.tableWrap}>
          <table className={styles.table}>
            <thead>
              <tr>
                <th>Timestamp</th>
                <th>Severity</th>
                <th>Source</th>
                <th>Message</th>
              </tr>
            </thead>
            <tbody>
              {entries.map((entry, index) => (
                <tr key={`${entry.timestamp}-${index}`}>
                  <td className={styles.timestamp}>{formatDateTime(entry.timestamp)}</td>
                  <td>
                    <span className={`${styles.severity} ${styles[SEVERITY_TEXT_CLASS[entry.severity]]}`}>
                      <StatusDot tone={SEVERITY_TONE[entry.severity]} />
                      {SEVERITY_LABEL[entry.severity]}
                    </span>
                  </td>
                  <td className={styles.source}>{entry.source}</td>
                  <td className={styles.message}>{entry.message}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  )
}
