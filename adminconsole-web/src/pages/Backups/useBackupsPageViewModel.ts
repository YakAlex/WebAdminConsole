import { useBackupsData } from '@/hooks/dashboard/useBackupsData'
import { BackupOutcome } from '@/lib/api/types'

/**
 * Audit step 11.3: previously only took `{ states }` from useBackupsData(),
 * discarding loading/error — a failed GET /api/backups looked identical to
 * "backups aren't configured". The same bug that was fixed on Logs.
 */
export function useBackupsPageViewModel() {
  const { states, loading, error } = useBackupsData()

  const successful = states.filter((j) => j.outcome === BackupOutcome.Ok).length
  const warnings = states.filter((j) => j.outcome === BackupOutcome.SizeWarning).length
  const failed = states.filter(
    (j) => j.outcome === BackupOutcome.Stale || j.outcome === BackupOutcome.Missing || j.outcome === BackupOutcome.Unknown,
  ).length
  const successRate = states.length > 0 ? Math.round((successful / states.length) * 100) : 0

  // Audit fix (2026-08-22, §3): the latest known size of each job, summed
  // across the whole system — the same "total backup size" shown on
  // Overview in WPF. history.at(-1) is the last confirmed sample; a job
  // with no successful cycle yet (empty history) contributes nothing.
  const totalSizeBytes = states.reduce((sum, job) => sum + (job.history.at(-1)?.sizeBytes ?? 0), 0)

  return { jobs: states, successRate, successful, warnings, failed, totalSizeBytes, loading, error }
}
