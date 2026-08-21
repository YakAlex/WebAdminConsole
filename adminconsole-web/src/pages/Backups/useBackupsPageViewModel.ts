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

  // Аудит-фікс (2026-08-22, п.3): найновіший відомий розмір кожного job,
  // просумований по всій системі — той самий "total backup size", що був
  // на Overview у WPF. history.at(-1) — останній підтверджений семпл;
  // job без жодного успішного циклу ще (порожня history) не додає нічого.
  const totalSizeBytes = states.reduce((sum, job) => sum + (job.history.at(-1)?.sizeBytes ?? 0), 0)

  return { jobs: states, successRate, successful, warnings, failed, totalSizeBytes, loading, error }
}
