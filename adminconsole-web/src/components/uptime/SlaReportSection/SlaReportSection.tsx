import { useState } from 'react'
import { FileText } from 'lucide-react'
import { slaReportHtmlUrl, type SlaReportQuery } from '@/lib/api/endpoints'
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
export function SlaReportSection() {
  const [from, setFrom] = useState(defaultFrom())
  const [to, setTo] = useState(defaultTo())
  const [group, setGroup] = useState('')
  const [server, setServer] = useState('')

  const buildQuery = (): SlaReportQuery => ({
    from: toIsoDayStart(from),
    to: toIsoDayEnd(to),
    group: group.trim() || undefined,
    server: server.trim() || undefined,
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
        <label className={styles.field}>
          <span>From</span>
          <input type="date" className={styles.input} value={from} onChange={(e) => setFrom(e.target.value)} />
        </label>
        <label className={styles.field}>
          <span>To</span>
          <input type="date" className={styles.input} value={to} onChange={(e) => setTo(e.target.value)} />
        </label>
        <label className={styles.field}>
          <span>Group</span>
          <input
            type="text"
            className={styles.input}
            placeholder="All groups"
            value={group}
            onChange={(e) => setGroup(e.target.value)}
          />
        </label>
        <label className={styles.field}>
          <span>Server</span>
          <input
            type="text"
            className={styles.input}
            placeholder="All servers"
            value={server}
            onChange={(e) => setServer(e.target.value)}
          />
        </label>
        <button type="button" className={styles.generateButton} onClick={generate}>
          Generate SLA Report
        </button>
      </div>
    </div>
  )
}
