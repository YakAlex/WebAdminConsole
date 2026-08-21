import type { DowntimeRecord } from '@/lib/api/types'

export interface UptimeSeries {
  /** % аптайму за весь період вікна. */
  percent: number
  /** % аптайму по кожному bucket'у — для sparkline-тренду. */
  buckets: number[]
}

/**
 * Реальний % аптайму та bucketed-тренд з фактичних DowntimeRecord —
 * без жодних захардкоджених/фейкових чисел (Крок 3, заміна моків).
 *
 * Для кожного bucket рахується сумарний downtime (мс) усіх релевантних
 * інцидентів, що перетинаються з ним, поділений на "ідеальний" час
 * bucket'а (bucketMs * кількість пристроїв — 1, якщо рахуємо один сервер).
 */
export function computeUptimeSeries(
  records: DowntimeRecord[],
  deviceCount: number,
  options: { hours?: number; buckets?: number; serverIp?: string } = {},
): UptimeSeries {
  const { hours = 24, buckets = 12, serverIp } = options
  const now = Date.now()
  const windowStart = now - hours * 60 * 60 * 1000
  const bucketMs = (hours * 60 * 60 * 1000) / buckets

  const relevant = serverIp ? records.filter((r) => r.serverIp === serverIp) : records
  const perBucketDeviceCount = serverIp ? 1 : Math.max(deviceCount, 1)

  const bucketDowntimeMs = new Array<number>(buckets).fill(0)

  for (const record of relevant) {
    const fellAt = new Date(record.fellAt).getTime()
    const recoveredAt = record.recoveredAt ? new Date(record.recoveredAt).getTime() : now

    for (let i = 0; i < buckets; i++) {
      const bucketStart = windowStart + i * bucketMs
      const bucketEnd = bucketStart + bucketMs
      const overlapMs = Math.min(recoveredAt, bucketEnd) - Math.max(fellAt, bucketStart)
      if (overlapMs > 0) bucketDowntimeMs[i] += overlapMs
    }
  }

  const perBucketCapacityMs = bucketMs * perBucketDeviceCount
  const bucketPercents = bucketDowntimeMs.map((downMs) =>
    clampPercent(100 - (downMs / perBucketCapacityMs) * 100),
  )

  const totalDownMs = bucketDowntimeMs.reduce((sum, v) => sum + v, 0)
  const totalCapacityMs = perBucketCapacityMs * buckets
  const percent = totalCapacityMs > 0 ? clampPercent(100 - (totalDownMs / totalCapacityMs) * 100) : 100

  return { percent, buckets: bucketPercents }
}

/** Мітки часу для осі під sparkline (5 точок — той самий крок, що в референсі). */
export function computeUptimeAxisLabels(hours = 24, points = 5): string[] {
  const now = Date.now()
  const windowStart = now - hours * 60 * 60 * 1000
  const stepMs = (hours * 60 * 60 * 1000) / (points - 1)

  return Array.from({ length: points }, (_, i) =>
    new Date(windowStart + i * stepMs).toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' }),
  )
}

/** Кількість інцидентів, що почались у вікні (за замовчуванням — останні 24г). */
export function countIncidentsInWindow(records: DowntimeRecord[], hours = 24, serverIp?: string): number {
  const windowStart = Date.now() - hours * 60 * 60 * 1000
  return records.filter((r) => (!serverIp || r.serverIp === serverIp) && new Date(r.fellAt).getTime() >= windowStart)
    .length
}

function clampPercent(value: number): number {
  return Math.max(0, Math.min(100, value))
}
