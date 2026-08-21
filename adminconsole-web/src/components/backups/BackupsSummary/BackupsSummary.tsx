import { Database, CircleCheck, TriangleAlert, CircleX } from 'lucide-react'
import { Card, CardHeader } from '@/components/ui/Card'
import styles from './BackupsSummary.module.scss'

export interface BackupsSummaryProps {
  successRate: number
  successful: number
  warnings: number
  failed: number
}

/** §26 брифу (Backups): "Backup health" + "Success rate" — великий KPI на всю ширину. */
export function BackupsSummary({ successRate, successful, warnings, failed }: BackupsSummaryProps) {
  return (
    <Card>
      <CardHeader eyebrow="Backup Health" icon={<Database size={14} strokeWidth={1.75} />} />

      <div className={styles.headline}>
        <span className={styles.kpi}>{successRate}%</span>
        <span className={styles.kpiLabel}>Success rate</span>
      </div>

      <div className={styles.stats}>
        <span className={`${styles.stat} ${styles.statSuccess}`}>
          <CircleCheck size={14} strokeWidth={1.75} />
          {successful} Successful
        </span>
        <span className={`${styles.stat} ${styles.statWarning}`}>
          <TriangleAlert size={14} strokeWidth={1.75} />
          {warnings} Warnings
        </span>
        <span className={`${styles.stat} ${styles.statCritical}`}>
          <CircleX size={14} strokeWidth={1.75} />
          {failed} Failed
        </span>
      </div>
    </Card>
  )
}
