import { ScrollText } from 'lucide-react'
import { Card, CardHeader } from '@/components/ui/Card'
import styles from './LogSeveritySummary.module.scss'

export interface LogSeveritySummaryProps {
  info: number
  success: number
  warning: number
  error: number
}

/** §26 брифу (Logs): "Log severity summary". */
export function LogSeveritySummary({ info, success, warning, error }: LogSeveritySummaryProps) {
  return (
    <Card>
      <CardHeader eyebrow="Recent Log Activity" icon={<ScrollText size={14} strokeWidth={1.75} />} />

      <div className={styles.stats}>
        <div className={styles.stat}>
          <span className={styles.statValue}>{info}</span>
          <span className={styles.statLabel}>Info</span>
        </div>
        <div className={styles.stat}>
          <span className={styles.statValue}>{success}</span>
          <span className={styles.statLabel}>Success</span>
        </div>
        <div className={styles.stat}>
          <span className={styles.statValue}>{warning}</span>
          <span className={styles.statLabel}>Warning</span>
        </div>
        <div className={styles.stat}>
          <span className={styles.statValue}>{error}</span>
          <span className={styles.statLabel}>Error</span>
        </div>
      </div>
    </Card>
  )
}
