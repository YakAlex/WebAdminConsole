import { Wrench, CalendarCheck } from 'lucide-react'
import type { MaintenanceWindow } from '@/lib/api/types'
import { formatMaintenanceSchedule } from '@/lib/format'
import styles from './MaintenanceCard.module.scss'

export interface MaintenanceCardProps {
  windows: MaintenanceWindow[]
}

/** §15 брифу. Дані — з useMaintenanceWindows() (SignalR MaintenanceChangedOccurred, без REST-знімка). */
export function MaintenanceCard({ windows }: MaintenanceCardProps) {
  return (
    <div className={styles.card}>
      <span className={styles.eyebrow}>
        <Wrench size={14} strokeWidth={1.75} />
        Maintenance
      </span>

      <div className={styles.headline}>
        <span className={styles.count}>{windows.length}</span>
        <span className={styles.countLabel}>Upcoming windows</span>
      </div>

      <div className={styles.divider} />

      {windows.length === 0 ? (
        <div className={styles.empty}>
          <CalendarCheck size={18} strokeWidth={1.5} className={styles.emptyIcon} />
          <span className={styles.emptyText}>No scheduled maintenance</span>
          <span className={styles.emptyClear}>All systems available</span>
        </div>
      ) : (
        <div className={styles.windows}>
          {windows.map((item) => (
            <div className={styles.window} key={`${item.displayName}-${item.from}`}>
              <div className={styles.windowTitle}>{item.displayName}</div>
              <div className={styles.windowMeta}>{formatMaintenanceSchedule(item.from, item.to)}</div>
            </div>
          ))}
        </div>
      )}
    </div>
  )
}
