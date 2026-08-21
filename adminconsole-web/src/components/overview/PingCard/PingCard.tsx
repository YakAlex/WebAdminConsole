import { Activity, CircleCheck, TriangleAlert, Loader2 } from 'lucide-react'
import clsx from 'clsx'
import styles from './PingCard.module.scss'

export interface PingCardProps {
  online: number
  total: number
  successRate: number
  offline: number
  hasData: boolean
}

/** §10 брифу. Дані — з usePingStream() (SignalR PingBatchResultOccurred), без REST-знімка. */
export function PingCard({ online, total, successRate, offline, hasData }: PingCardProps) {
  return (
    <div className={styles.card}>
      <span className={styles.eyebrow}>
        <Activity size={14} strokeWidth={1.75} />
        Ping
      </span>

      <div className={styles.headline}>
        <span className={styles.count}>
          {online} / {total}
        </span>
        <span
          className={clsx(styles.status, !hasData && styles.statusMuted, hasData && offline > 0 && styles.statusWarning)}
        >
          {!hasData ? 'WAITING' : offline > 0 ? 'DEGRADED' : 'ONLINE'}
        </span>
      </div>

      <div className={styles.bar}>
        <div className={styles.barFill} style={{ width: `${successRate}%` }} />
      </div>

      <div className={styles.stats}>
        <div className={styles.stat}>
          <span className={styles.statValue}>{successRate}%</span>
          <span className={styles.statLabel}>Success rate</span>
        </div>
        <div className={styles.stat}>
          <span className={styles.statValue}>{offline}</span>
          <span className={styles.statLabel}>Offline</span>
        </div>
      </div>

      <div className={styles.footer}>
        {!hasData ? (
          <>
            <Loader2 size={14} strokeWidth={1.75} className={styles.footerIcon} />
            Waiting for first ping cycle…
          </>
        ) : offline === 0 ? (
          <>
            <CircleCheck size={14} strokeWidth={1.75} className={styles.footerIcon} />
            All servers reachable
          </>
        ) : (
          <>
            <TriangleAlert size={14} strokeWidth={1.75} className={styles.footerIconWarning} />
            {offline} server{offline === 1 ? '' : 's'} unreachable
          </>
        )}
      </div>
    </div>
  )
}
