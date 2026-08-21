import { HeartPulse, Activity, Clock, Database } from 'lucide-react'
import { HealthRing } from '@/components/ui/HealthRing'
import { StatusDot } from '@/components/ui/StatusDot'
import styles from './SystemHealthHero.module.scss'

export interface SystemHealthHeroProps {
  online: number
  total: number
  hasPingData: boolean
  pingSuccessRate: number
  uptimePercent: number
  backupsSuccessful: number
  backupsTotal: number
  criticalAlerts: number
  warnings: number
}

/**
 * §7 брифу: головний Hero-блок dashboard'у — health ring зліва, три KPI
 * метрики справа (розділені вертикальними divider'ами), semantic статус
 * унизу. Займає ~65–70% ширини — решту бере AttentionRequired.
 */
export function SystemHealthHero({
  online,
  total,
  hasPingData,
  pingSuccessRate,
  uptimePercent,
  backupsSuccessful,
  backupsTotal,
  criticalAlerts,
  warnings,
}: SystemHealthHeroProps) {
  const healthPercent = total > 0 ? (online / total) * 100 : 0
  const allHealthy = hasPingData && online === total && criticalAlerts === 0 && warnings === 0

  return (
    <div className={styles.card}>
      <span className={styles.eyebrow}>System Health</span>

      <div className={styles.top}>
        <div className={styles.status}>
          <HealthRing percent={healthPercent} size={92} strokeWidth={6}>
            <HeartPulse size={24} strokeWidth={1.75} className={styles.ringIcon} />
          </HealthRing>
          <div>
            <div className={styles.statusCount}>
              {online} / {total}
            </div>
            <div className={styles.statusLabel}>{hasPingData ? 'ONLINE' : 'WAITING'}</div>
            <div className={styles.statusHint}>
              {hasPingData ? `${healthPercent.toFixed(0)}% of systems are healthy` : 'Waiting for first ping cycle…'}
            </div>
          </div>
        </div>

        <div className={styles.metrics}>
          <div className={styles.metric}>
            <span className={styles.metricLabel}>
              <Activity size={14} strokeWidth={1.75} />
              Ping
            </span>
            <span className={styles.metricValue}>{hasPingData ? `${pingSuccessRate}%` : '—'}</span>
            <span className={styles.metricCaption}>Success rate</span>
          </div>
          <div className={styles.metric}>
            <span className={styles.metricLabel}>
              <Clock size={14} strokeWidth={1.75} />
              Uptime
            </span>
            <span className={styles.metricValue}>{uptimePercent.toFixed(2)}%</span>
            <span className={styles.metricCaption}>Overall uptime</span>
          </div>
          <div className={styles.metric}>
            <span className={styles.metricLabel}>
              <Database size={14} strokeWidth={1.75} />
              Backups
            </span>
            <span className={styles.metricValue}>
              {backupsSuccessful} / {backupsTotal}
            </span>
            <span className={styles.metricCaption}>Successful</span>
          </div>
        </div>
      </div>

      <div className={styles.footer}>
        <span className={styles.footerTitle}>
          <StatusDot tone={allHealthy ? 'success' : 'warning'} />
          {allHealthy ? 'All systems are operational' : 'Some systems need attention'}
        </span>
        <span className={styles.footerCaption}>
          {allHealthy ? 'No issues detected across your infrastructure.' : 'Review Attention Required for details.'}
        </span>
      </div>
    </div>
  )
}
