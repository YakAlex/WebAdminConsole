import { Archive } from 'lucide-react'
import clsx from 'clsx'
import { StatusDot, type StatusTone } from '@/components/ui/StatusDot'
import { Sparkline } from '@/components/ui/Sparkline'
import { BackupKind, BackupOutcome, type BackupCheckState } from '@/lib/api/types'
import { formatBytes, formatClock } from '@/lib/format'
import styles from './BackupJobsTable.module.scss'

const KIND_LABEL: Record<BackupKind, string> = {
  [BackupKind.Full]: 'Full',
  [BackupKind.Diff]: 'Diff',
}

const OUTCOME_LABEL: Record<BackupOutcome, string> = {
  [BackupOutcome.Ok]: 'Success',
  [BackupOutcome.SizeWarning]: 'Size warning',
  [BackupOutcome.Stale]: 'Stale',
  [BackupOutcome.Missing]: 'Missing',
  [BackupOutcome.Unknown]: 'Unknown',
}

const OUTCOME_TONE: Record<BackupOutcome, StatusTone> = {
  [BackupOutcome.Ok]: 'success',
  [BackupOutcome.SizeWarning]: 'warning',
  [BackupOutcome.Stale]: 'critical',
  [BackupOutcome.Missing]: 'critical',
  [BackupOutcome.Unknown]: 'inactive',
}

const OUTCOME_TEXT_CLASS: Record<StatusTone, string> = {
  success: 'statusSuccess',
  warning: 'statusWarning',
  critical: 'statusCritical',
  inactive: 'statusInactive',
  info: 'statusInactive',
}

export interface BackupJobsTableProps {
  jobs: BackupCheckState[]
}

/** §26 брифу (Backups): "Jobs table" — детальніша за компактну версію Overview: Kind, size-тренд з History, Last error. */
export function BackupJobsTable({ jobs }: BackupJobsTableProps) {
  return (
    <div className={styles.card}>
      <div className={styles.header}>
        <span className={styles.eyebrow}>Jobs</span>
      </div>

      {jobs.length === 0 ? (
        <div className={styles.empty}>No backup checks configured</div>
      ) : (
        <div className={styles.tableWrap}>
          <table className={styles.table}>
            <thead>
              <tr>
                <th>Job</th>
                <th>Host</th>
                <th>Status</th>
                <th>Size</th>
                <th>Size trend</th>
                <th>Last run</th>
                <th>Last error</th>
              </tr>
            </thead>
            <tbody>
              {jobs.map((job) => (
                <tr key={`${job.name}-${job.kind}`}>
                  <td>
                    <span className={styles.job}>
                      <Archive size={14} strokeWidth={1.75} className={styles.jobIcon} />
                      {job.name}
                      <span className={styles.kind}>{KIND_LABEL[job.kind]}</span>
                    </span>
                  </td>
                  <td className={styles.host}>{job.host}</td>
                  <td>
                    <span className={clsx(styles.status, styles[OUTCOME_TEXT_CLASS[OUTCOME_TONE[job.outcome]]])}>
                      <StatusDot tone={OUTCOME_TONE[job.outcome]} />
                      {OUTCOME_LABEL[job.outcome]}
                    </span>
                  </td>
                  <td className={styles.size}>{formatBytes(job.history.at(-1)?.sizeBytes)}</td>
                  <td className={styles.trendCell}>
                    {job.history.length >= 2 ? (
                      <Sparkline
                        data={job.history.map((s) => s.sizeBytes)}
                        width={64}
                        height={20}
                        strokeWidth={1.25}
                        color="var(--color-brand-cyan)"
                      />
                    ) : (
                      '—'
                    )}
                  </td>
                  <td className={styles.lastRun}>{formatClock(job.lastConfirmedAt)}</td>
                  <td className={styles.error}>{job.lastError ?? '—'}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  )
}
