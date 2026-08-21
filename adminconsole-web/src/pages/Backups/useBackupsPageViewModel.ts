import { useBackupsData } from '@/hooks/dashboard/useBackupsData'
import { BackupOutcome } from '@/lib/api/types'

/**
 * Крок 11.3 аудиту: раніше брало лише `{ states }` з useBackupsData(),
 * відкидаючи loading/error — збій GET /api/backups виглядав ідентично
 * "бекапів не налаштовано". Той самий баг, що фіксили на Logs.
 */
export function useBackupsPageViewModel() {
  const { states, loading, error } = useBackupsData()

  const successful = states.filter((j) => j.outcome === BackupOutcome.Ok).length
  const warnings = states.filter((j) => j.outcome === BackupOutcome.SizeWarning).length
  const failed = states.filter(
    (j) => j.outcome === BackupOutcome.Stale || j.outcome === BackupOutcome.Missing || j.outcome === BackupOutcome.Unknown,
  ).length
  const successRate = states.length > 0 ? Math.round((successful / states.length) * 100) : 0

  return { jobs: states, successRate, successful, warnings, failed, loading, error }
}
