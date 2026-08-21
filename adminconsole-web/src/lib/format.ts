/** "12:43" з ISO-рядка (DateTimeOffset), у локальному часі браузера. */
export function formatClock(iso: string | null | undefined): string {
  if (!iso) return '—'
  return new Date(iso).toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' })
}

/** "12:43:10" — той самий формат, що в референсі для "Last check". */
export function formatClockWithSeconds(iso: string | null | undefined): string {
  if (!iso) return '—'
  return new Date(iso).toLocaleTimeString(undefined, {
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
  })
}

/** "Aug 18 · 23:00" — для вікон обслуговування. */
export function formatMaintenanceSchedule(from: string, to: string | null): string {
  const fromDate = new Date(from)
  const dateLabel = fromDate.toLocaleDateString(undefined, { month: 'short', day: 'numeric' })
  const fromTime = fromDate.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' })

  if (!to) return `${dateLabel} · ${fromTime}`

  const toDate = new Date(to)
  const toTime = toDate.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' })
  return `${dateLabel} · ${fromTime}–${toTime}`
}

/** "Aug 21, 13:45:02" — для Logs (може охоплювати кілька діб, потрібна дата). */
export function formatDateTime(iso: string | null | undefined): string {
  if (!iso) return '—'
  const date = new Date(iso)
  const dateLabel = date.toLocaleDateString(undefined, { month: 'short', day: 'numeric' })
  const timeLabel = date.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit', second: '2-digit' })
  return `${dateLabel}, ${timeLabel}`
}

export function truncate(text: string, maxLength: number): string {
  return text.length > maxLength ? `${text.slice(0, maxLength - 1)}…` : text
}

/** "2d 3h 14m" / "12m" — тривалість інциденту; recoveredAt=null → рахує до "зараз" (відкритий інцидент). */
export function formatDuration(fellAt: string, recoveredAt: string | null): string {
  const start = new Date(fellAt).getTime()
  const end = recoveredAt ? new Date(recoveredAt).getTime() : Date.now()
  const totalMinutes = Math.max(0, Math.floor((end - start) / 60_000))

  const days = Math.floor(totalMinutes / 1440)
  const hours = Math.floor((totalMinutes % 1440) / 60)
  const minutes = totalMinutes % 60

  if (days > 0) return `${days}d ${hours}h ${minutes}m`
  if (hours > 0) return `${hours}h ${minutes}m`
  return `${minutes}m`
}

/**
 * "1d 2h 30m" з .NET TimeSpan "c"-формату (напр. "1.02:30:00") — саме так
 * System.Text.Json за замовчуванням серіалізує TimeSpan (перевірено
 * емпірично на .NET 8, це не ISO 8601 duration). Пріоритет 3, #3.2 (SLA-звіт).
 */
export function formatTimeSpan(value: string | null | undefined): string {
  if (!value) return '—'
  const match = value.match(/^(-)?(?:(\d+)\.)?(\d{2}):(\d{2}):(\d{2})/)
  if (!match) return value

  const sign = match[1] ? '-' : ''
  const days = Number(match[2] ?? 0)
  const hours = Number(match[3])
  const minutes = Number(match[4])

  if (days > 0) return `${sign}${days}d ${hours}h ${minutes}m`
  if (hours > 0) return `${sign}${hours}h ${minutes}m`
  return `${sign}${minutes}m`
}

/** "2.34 GB" / "412.5 MB" — той самий поріг/округлення, що WPF BackupRowViewModel.FormatSize. */
export function formatBytes(bytes: number | null | undefined): string {
  if (bytes == null) return '—'
  const gb = bytes / 1024 / 1024 / 1024
  if (gb >= 1) return `${gb.toFixed(2)} GB`

  const mb = bytes / 1024 / 1024
  return `${mb.toFixed(1)} MB`
}
