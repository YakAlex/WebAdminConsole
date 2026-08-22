import { Clock, MonitorCheck, TriangleAlert } from 'lucide-react'
import { Card, CardHeader } from '@/components/ui/Card'
import { Sparkline } from '@/components/ui/Sparkline'
import styles from './GlobalUptimeHealth.module.scss'

export interface GlobalUptimeHealthProps {
  overallPercent: number
  trend: number[]
  axisLabels: string[]
  monitoredDevices: number
  incidentsInWindow: number
}

/** Brief §26 (Uptime): "99.98% global uptime" + chart — a large, full-width KPI block. */
export function GlobalUptimeHealth({
  overallPercent,
  trend,
  axisLabels,
  monitoredDevices,
  incidentsInWindow,
}: GlobalUptimeHealthProps) {
  return (
    <Card>
      <CardHeader eyebrow="Global Uptime" icon={<Clock size={14} strokeWidth={1.75} />} />

      <div className={styles.headline}>
        <span className={styles.kpi}>{overallPercent.toFixed(2)}%</span>
        <span className={styles.kpiLabel}>Overall uptime — last 24h</span>
      </div>

      <Sparkline
        data={trend}
        variant="area"
        width={1000}
        height={80}
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
        <span className={styles.footerItem}>
          <MonitorCheck size={14} strokeWidth={1.75} className={styles.footerIcon} />
          {monitoredDevices} monitored device{monitoredDevices === 1 ? '' : 's'}
        </span>
        <span className={styles.footerItem}>
          <TriangleAlert
            size={14}
            strokeWidth={1.75}
            className={incidentsInWindow > 0 ? styles.footerIconWarning : styles.footerIcon}
          />
          {incidentsInWindow} incident{incidentsInWindow === 1 ? '' : 's'} in last 24h
        </span>
      </div>
    </Card>
  )
}
