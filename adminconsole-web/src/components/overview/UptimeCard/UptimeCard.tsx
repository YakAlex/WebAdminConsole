import { Clock, MonitorCheck } from 'lucide-react'
import { Sparkline } from '@/components/ui/Sparkline'
import styles from './UptimeCard.module.scss'

export interface UptimeCardProps {
  overallPercent: number
  monitoredDevices: number
  trend: number[]
  axisLabels: string[]
}

/** §11 брифу. Тренд рахується з реальних DowntimeRecord — див. hooks/dashboard/uptimeMath.ts. */
export function UptimeCard({ overallPercent, monitoredDevices, trend, axisLabels }: UptimeCardProps) {
  return (
    <div className={styles.card}>
      <div className={styles.header}>
        <span className={styles.eyebrow}>
          <Clock size={14} strokeWidth={1.75} />
          Uptime
        </span>
        <span className={styles.rangeLabel}>24h</span>
      </div>

      <div className={styles.headline}>
        <span className={styles.kpi}>{overallPercent.toFixed(2)}%</span>
        <span className={styles.kpiLabel}>Overall uptime</span>
      </div>

      <Sparkline
        data={trend}
        variant="area"
        width={400}
        height={42}
        strokeWidth={1.5}
        color="var(--color-brand-cyan)"
        className={styles.chart}
      />
      <div className={styles.axis}>
        {axisLabels.map((label, index) => (
          <span key={`${label}-${index}`}>{label}</span>
        ))}
      </div>

      <div className={styles.footer}>
        <MonitorCheck size={14} strokeWidth={1.75} className={styles.footerIcon} />
        {monitoredDevices} monitored device{monitoredDevices === 1 ? '' : 's'}
      </div>
    </div>
  )
}
