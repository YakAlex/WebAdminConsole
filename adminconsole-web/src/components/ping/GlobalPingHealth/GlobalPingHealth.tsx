import { Activity, HeartPulse } from 'lucide-react'
import clsx from 'clsx'
import { Card, CardHeader } from '@/components/ui/Card'
import { HealthRing } from '@/components/ui/HealthRing'
import { StatusDot } from '@/components/ui/StatusDot'
import styles from './GlobalPingHealth.module.scss'

export interface GlobalPingHealthProps {
  online: number
  total: number
  offline: number
  successRate: number
  avgLatencyMs: number | null
  hasData: boolean
}

/**
 * §26 брифу (Ping): "Global ping health" — великий KPI-блок вгорі
 * вкладки. Перевикористовує HealthRing/StatusDot/Card з Design System
 * замість власної card-surface розмітки.
 */
export function GlobalPingHealth({ online, total, offline, successRate, avgLatencyMs, hasData }: GlobalPingHealthProps) {
  const healthPercent = total > 0 ? (online / total) * 100 : 0
  const allOnline = hasData && offline === 0

  return (
    <Card>
      <CardHeader eyebrow="Global Ping Health" icon={<Activity size={14} strokeWidth={1.75} />} />

      <div className={styles.body}>
        <div className={styles.ringBlock}>
          <HealthRing percent={healthPercent} size={112} strokeWidth={7}>
            <HeartPulse size={28} strokeWidth={1.75} className={styles.ringIcon} />
          </HealthRing>
          <div>
            <div className={styles.count}>
              {online} / {total}
            </div>
            <div
              className={clsx(
                styles.statusLabel,
                !hasData && styles.statusMuted,
                hasData && offline > 0 && styles.statusWarning,
              )}
            >
              {!hasData ? 'WAITING' : offline > 0 ? 'DEGRADED' : 'ONLINE'}
            </div>
          </div>
        </div>

        <div className={styles.stats}>
          <div className={styles.stat}>
            <span className={styles.statValue}>{hasData ? `${successRate}%` : '—'}</span>
            <span className={styles.statLabel}>Success rate</span>
          </div>
          <div className={styles.stat}>
            <span className={styles.statValue}>{avgLatencyMs != null ? `${avgLatencyMs}ms` : '—'}</span>
            <span className={styles.statLabel}>Avg latency</span>
          </div>
          <div className={styles.stat}>
            <span className={styles.statValue}>{offline}</span>
            <span className={styles.statLabel}>Offline</span>
          </div>
        </div>
      </div>

      <div className={styles.footer}>
        <StatusDot tone={!hasData ? 'inactive' : allOnline ? 'success' : 'critical'} />
        <span>
          {!hasData
            ? 'Waiting for first ping cycle…'
            : allOnline
              ? 'All hosts reachable'
              : `${offline} host${offline === 1 ? '' : 's'} unreachable`}
        </span>
      </div>
    </Card>
  )
}
