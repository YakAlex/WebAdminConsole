import { Info, CircleCheck, TriangleAlert, CircleX, ChevronRight, type LucideIcon } from 'lucide-react'
import { useNavigate } from 'react-router-dom'
import clsx from 'clsx'
import { LogSeverity, type AppLogEntry } from '@/lib/api/types'
import { formatClock, truncate } from '@/lib/format'
import styles from './RecentActivity.module.scss'

type Tone = 'success' | 'info' | 'warning' | 'critical'

const SEVERITY_MAP: Record<LogSeverity, { icon: LucideIcon; tone: Tone }> = {
  [LogSeverity.Info]: { icon: Info, tone: 'info' },
  [LogSeverity.Success]: { icon: CircleCheck, tone: 'success' },
  [LogSeverity.Warning]: { icon: TriangleAlert, tone: 'warning' },
  [LogSeverity.Error]: { icon: CircleX, tone: 'critical' },
}

export interface RecentActivityProps {
  entries: AppLogEntry[]
}

/** Brief §12: a vertical event feed with thin dividers. Data comes from useAppLogEntries(). */
export function RecentActivity({ entries }: RecentActivityProps) {
  const navigate = useNavigate()

  return (
    <div className={styles.card}>
      <span className={styles.eyebrow}>Recent Activity</span>

      {entries.length === 0 ? (
        <div className={styles.empty}>No activity yet</div>
      ) : (
        <div className={styles.list}>
          {entries.slice(0, 5).map((entry) => {
            const { icon: Icon, tone } = SEVERITY_MAP[entry.severity]
            return (
              <div className={styles.row} key={`${entry.timestamp}-${entry.source}`}>
                <span className={clsx(styles.iconBadge, styles[tone])}>
                  <Icon size={15} strokeWidth={1.75} />
                </span>
                <div className={styles.body}>
                  <div className={styles.title}>{truncate(entry.message, 60)}</div>
                  <div className={styles.subtitle}>{entry.source}</div>
                </div>
                <span className={styles.time}>{formatClock(entry.timestamp)}</span>
              </div>
            )
          })}
        </div>
      )}

      <div className={styles.footer}>
        <button type="button" className={styles.footerLink} onClick={() => navigate('/logs')}>
          View all activity
          <ChevronRight size={13} strokeWidth={2} />
        </button>
      </div>
    </div>
  )
}
