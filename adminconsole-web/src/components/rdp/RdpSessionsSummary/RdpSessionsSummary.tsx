import { SquareTerminal } from 'lucide-react'
import { Card, CardHeader } from '@/components/ui/Card'
import { formatClock } from '@/lib/format'
import type { LastLogout } from '@/hooks/dashboard/useRdpSessions'
import styles from './RdpSessionsSummary.module.scss'

export interface RdpSessionsSummaryProps {
  activeCount: number
  uniqueUsers: number
  dailyPeak: number
  lastLogout: LastLogout | null
}

/** §26 брифу (RDP): підсумок активних сесій — реальні поля з RdpSessionsPayload (globalDailyPeak/lastLogout*). */
export function RdpSessionsSummary({ activeCount, uniqueUsers, dailyPeak, lastLogout }: RdpSessionsSummaryProps) {
  return (
    <Card>
      <CardHeader eyebrow="RDP Sessions" icon={<SquareTerminal size={14} strokeWidth={1.75} />} />

      <div className={styles.stats}>
        <div className={styles.stat}>
          <span className={styles.statValue}>{activeCount}</span>
          <span className={styles.statLabel}>Active sessions</span>
        </div>
        <div className={styles.stat}>
          <span className={styles.statValue}>{uniqueUsers}</span>
          <span className={styles.statLabel}>Unique users</span>
        </div>
        <div className={styles.stat}>
          <span className={styles.statValue}>{dailyPeak}</span>
          <span className={styles.statLabel}>Peak today</span>
        </div>
        <div className={styles.stat}>
          <span className={styles.statValue}>{lastLogout ? formatClock(lastLogout.at) : '—'}</span>
          <span className={styles.statLabel}>
            {lastLogout ? `Last logout — ${lastLogout.username} (${lastLogout.serverName})` : 'Last logout'}
          </span>
        </div>
      </div>
    </Card>
  )
}
