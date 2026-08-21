import { Database, CircleCheck, TriangleAlert, CircleX } from 'lucide-react'
import { useNavigate } from 'react-router-dom'
import clsx from 'clsx'
import { StatusDot, type StatusTone } from '@/components/ui/StatusDot'
import { ServiceDisabledNotice } from '@/components/ui/ServiceDisabledNotice'
import { BackupOutcome, type BackupCheckState } from '@/lib/api/types'
import { formatClock } from '@/lib/format'
import styles from './BackupsCard.module.scss'

const STATUS_TEXT_CLASS: Record<StatusTone, string> = {
  success: 'statusSuccess',
  warning: 'statusWarning',
  critical: 'statusCritical',
  inactive: 'statusInactive',
  info: 'statusInactive',
}

const STATUS_LABEL: Record<BackupOutcome, string> = {
  [BackupOutcome.Ok]: 'Success',
  [BackupOutcome.SizeWarning]: 'Warning',
  [BackupOutcome.Stale]: 'Stale',
  [BackupOutcome.Missing]: 'Missing',
  [BackupOutcome.Unknown]: 'Unknown',
}

const STATUS_TONE: Record<BackupOutcome, StatusTone> = {
  [BackupOutcome.Ok]: 'success',
  [BackupOutcome.SizeWarning]: 'warning',
  [BackupOutcome.Stale]: 'critical',
  [BackupOutcome.Missing]: 'critical',
  [BackupOutcome.Unknown]: 'inactive',
}

export interface BackupsCardProps {
  jobs: BackupCheckState[]
  /** Аудит-фікс п.4: Backup Monitor вимкнено в Settings — jobs вже занулено викликачем. */
  disabled?: boolean
}

/** §13 брифу. Дані — з useBackupsData() (GET /api/backups + BackupStatusUpdatedOccurred). */
export function BackupsCard({ jobs, disabled }: BackupsCardProps) {
  const navigate = useNavigate()
  const successful = jobs.filter((j) => j.outcome === BackupOutcome.Ok).length
  const warnings = jobs.filter((j) => j.outcome === BackupOutcome.SizeWarning).length
  const failed = jobs.filter(
    (j) => j.outcome === BackupOutcome.Stale || j.outcome === BackupOutcome.Missing || j.outcome === BackupOutcome.Unknown,
  ).length

  return (
    <div className={styles.card}>
      <div className={styles.header}>
        <span className={styles.eyebrow}>
          <Database size={14} strokeWidth={1.75} />
          Backups
        </span>
        <button type="button" className={styles.headerLink} onClick={() => navigate('/backups')}>
          View all
        </button>
      </div>

      {disabled ? (
        <ServiceDisabledNotice service="Backup Monitor" />
      ) : (
        <>
          <div className={styles.list}>
            {jobs.length === 0 ? (
              <div className={styles.empty}>No backup checks configured</div>
            ) : (
              jobs.slice(0, 4).map((job) => (
                <div className={styles.row} key={`${job.name}-${job.kind}`}>
                  <span className={styles.name}>{job.name}</span>
                  <span className={clsx(styles.status, styles[STATUS_TEXT_CLASS[STATUS_TONE[job.outcome]]])}>
                    <StatusDot tone={STATUS_TONE[job.outcome]} />
                    {STATUS_LABEL[job.outcome]}
                  </span>
                  <span className={styles.time}>{formatClock(job.lastConfirmedAt)}</span>
                </div>
              ))
            )}
          </div>

          <div className={styles.summary}>
            <span className={`${styles.summaryItem} ${styles.summarySuccess}`}>
              <CircleCheck size={14} strokeWidth={1.75} />
              {successful} Successful
            </span>
            <span className={`${styles.summaryItem} ${styles.summaryWarning}`}>
              <TriangleAlert size={14} strokeWidth={1.75} />
              {warnings} Warnings
            </span>
            <span className={`${styles.summaryItem} ${styles.summaryCritical}`}>
              <CircleX size={14} strokeWidth={1.75} />
              {failed} Failed
            </span>
          </div>
        </>
      )}
    </div>
  )
}
