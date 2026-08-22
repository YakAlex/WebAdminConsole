import { useMemo, useState } from 'react'
import { FileText } from 'lucide-react'
import clsx from 'clsx'
import { slaReportHtmlUrl, type SlaReportQuery } from '@/lib/api/endpoints'
import type { ServerEntry } from '@/lib/api/types'
import styles from './SlaReportSection.module.scss'

function toIsoDayStart(dateStr: string): string {
  return new Date(`${dateStr}T00:00:00`).toISOString()
}

/** "to" включно — весь обраний день, той самий підхід, що вже в Logs/Incidents фільтрах. */
function toIsoDayEnd(dateStr: string): string {
  return new Date(new Date(`${dateStr}T00:00:00`).getTime() + 24 * 60 * 60 * 1000).toISOString()
}

function defaultFrom(): string {
  const d = new Date()
  d.setDate(d.getDate() - 7)
  return d.toISOString().slice(0, 10)
}

function defaultTo(): string {
  return new Date().toISOString().slice(0, 10)
}

/**
 * Аудит-фікс п.3: більше ніякого inline-прев'ю таблиці прямо на сторінці
 * Uptime — клік на "Generate SLA Report" одразу відкриває готовий HTML-звіт
 * (GET /api/sla/html, той самий рендер що й у щотижневій Hangfire-джобі) у
 * НОВІЙ вкладці браузера. Тут лишається лише форма параметрів.
 */
export interface SlaReportSectionProps {
  servers: ServerEntry[]
}

export function SlaReportSection({ servers }: SlaReportSectionProps) {
  const [from, setFrom] = useState(defaultFrom())
  const [to, setTo] = useState(defaultTo())
  const [group, setGroup] = useState('')
  const [server, setServer] = useState('')

  const groups = useMemo(
    () => [...new Set(servers.map((s) => s.group))].sort((a, b) => a.localeCompare(b)),
    [servers],
  )

  // Список серверів звужується під обрану групу — не дає обрати сервер поза
  // фільтром групи (бекенд однаково застосував би обидва фільтри одночасно).
  const serverOptions = useMemo(
    () =>
      servers
        .filter((s) => !group || s.group === group)
        .map((s) => s.name)
        .sort((a, b) => a.localeCompare(b)),
    [servers, group],
  )

  const handleGroupChange = (value: string) => {
    setGroup(value)
    if (value && server && !servers.some((s) => s.name === server && s.group === value)) {
      setServer('')
    }
  }

  const buildQuery = (): SlaReportQuery => ({
    from: toIsoDayStart(from),
    to: toIsoDayEnd(to),
    group: group || undefined,
    server: server || undefined,
  })

  const generate = () => {
    window.open(slaReportHtmlUrl(buildQuery()), '_blank', 'noopener,noreferrer')
  }

  return (
    <div className={styles.card}>
      <div className={styles.header}>
        <span className={styles.eyebrow}>
          <FileText size={14} strokeWidth={1.75} />
          SLA Report
        </span>
      </div>

      <div className={styles.form}>
        <label className={clsx(styles.field, styles.fieldDate)}>
          <span>From</span>
          <input type="date" className={styles.input} value={from} onChange={(e) => setFrom(e.target.value)} />
        </label>
        <label className={clsx(styles.field, styles.fieldDate)}>
          <span>To</span>
          <input type="date" className={styles.input} value={to} onChange={(e) => setTo(e.target.value)} />
        </label>
        <label className={clsx(styles.field, styles.fieldText)}>
          <span>Group</span>
          <select className={styles.input} value={group} onChange={(e) => handleGroupChange(e.target.value)}>
            <option value="">All groups</option>
            {groups.map((g) => (
              <option key={g} value={g}>
                {g}
              </option>
            ))}
          </select>
        </label>
        <label className={clsx(styles.field, styles.fieldText)}>
          <span>Server</span>
          <select className={styles.input} value={server} onChange={(e) => setServer(e.target.value)}>
            <option value="">All servers</option>
            {serverOptions.map((name) => (
              <option key={name} value={name}>
                {name}
              </option>
            ))}
          </select>
        </label>
        <button type="button" className={styles.generateButton} onClick={generate}>
          Generate SLA Report
        </button>
      </div>
    </div>
  )
}
