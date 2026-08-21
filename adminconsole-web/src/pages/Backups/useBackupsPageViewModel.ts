import { useBackupsData } from '@/hooks/dashboard/useBackupsData'
import { BackupOutcome } from '@/lib/api/types'

export function useBackupsPageViewModel() {
  const { states } = useBackupsData()

  const successful = states.filter((j) => j.outcome === BackupOutcome.Ok).length
  const warnings = states.filter((j) => j.outcome === BackupOutcome.SizeWarning).length
  const failed = states.filter(
    (j) => j.outcome === BackupOutcome.Stale || j.outcome === BackupOutcome.Missing || j.outcome === BackupOutcome.Unknown,
  ).length
  const successRate = states.length > 0 ? Math.round((successful / states.length) * 100) : 0

  return { jobs: states, successRate, successful, warnings, failed }
}
