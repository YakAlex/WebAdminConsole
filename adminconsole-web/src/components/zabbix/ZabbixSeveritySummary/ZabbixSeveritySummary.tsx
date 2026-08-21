import { Bell, TriangleAlert } from 'lucide-react'
import { Card, CardHeader } from '@/components/ui/Card'
import styles from './ZabbixSeveritySummary.module.scss'

export interface ZabbixSeveritySummaryProps {
  critical: number
  warning: number
  info: number
  errorMessage: string | null
}

/** §26 брифу (Zabbix): "Critical / Warning / Resolved" — тут Critical/Warning/Informational (реальні Severity-групи Zabbix). */
export function ZabbixSeveritySummary({ critical, warning, info, errorMessage }: ZabbixSeveritySummaryProps) {
  return (
    <Card>
      <CardHeader eyebrow="Zabbix Alerts" icon={<Bell size={14} strokeWidth={1.75} />} />

      {errorMessage && (
        <div className={styles.banner}>
          <TriangleAlert size={14} strokeWidth={1.75} />
          Zabbix poller error: {errorMessage}
        </div>
      )}

      <div className={styles.stats}>
        <div className={styles.stat}>
          <span className={`${styles.statValue} ${styles.statCritical}`}>{critical}</span>
          <span className={styles.statLabel}>Critical (High/Disaster)</span>
        </div>
        <div className={styles.stat}>
          <span className={`${styles.statValue} ${styles.statWarning}`}>{warning}</span>
          <span className={styles.statLabel}>Warning (Average/Warning)</span>
        </div>
        <div className={styles.stat}>
          <span className={styles.statValue}>{info}</span>
          <span className={styles.statLabel}>Informational</span>
        </div>
      </div>
    </Card>
  )
}
