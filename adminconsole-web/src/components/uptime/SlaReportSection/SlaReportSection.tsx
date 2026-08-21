import { useState } from 'react'
import { FileText, Download } from 'lucide-react'
import { ErrorBanner } from '@/components/ui/ErrorBanner'
import { Spinner } from '@/components/ui/Spinner'
import { getSlaReport, slaReportHtmlUrl, type SlaReportQuery } from '@/lib/api/endpoints'
import { ApiError } from '@/lib/api/http'
import { formatTimeSpan } from '@/lib/format'
import type { SlaReport } from '@/lib/api/types'
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
 * Пріоритет 3, #3.2: бекенд (SlaController/SlaReportService) уже повністю
 * готовий — той самий рендер, що й у щотижневій Hangfire-джобі. Тут лише
 * форма + виклик готового ендпоінта, вбудовано прямо на сторінці Uptime
 * (рішення користувача — не модалка).
 */
export function SlaReportSection() {
  const [from, setFrom] = useState(defaultFrom())
  const [to, setTo] = useState(defaultTo())
  const [group, setGroup] = useState('')
  const [server, setServer] = useState('')
  const [report, setReport] = useState<SlaReport | null>(null)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<ApiError | null>(null)

  const buildQuery = (): SlaReportQuery => ({
    from: toIsoDayStart(from),
    to: toIsoDayEnd(to),
    group: group.trim() || undefined,
    server: server.trim() || undefined,
  })

  const generate = async () => {
    setLoading(true)
    setError(null)
    try {
      setReport(await getSlaReport(buildQuery()))
    } catch (err) {
      setError(err instanceof ApiError ? err : new ApiError(0, 'Unknown error'))
      setReport(null)
    } finally {
      setLoading(false)
    }
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
        <button type="button" className={styles.generateButton} onClick={generate} disabled={loading}>
          {loading ? 'Generating…' : 'Generate SLA Report'}
        </button>
      </div>

      {error && <ErrorBanner context="SLA report" error={error} />}
      {loading && <Spinner label="Generating report…" />}

      {report && !loading && (
        <>
          <div className={styles.summary}>
            <div className={styles.summaryStat}>
              <span className={styles.summaryValue}>
                {report.overallUptimePercent != null ? `${report.overallUptimePercent.toFixed(2)}%` : '—'}
              </span>
              <span className={styles.summaryLabel}>
                Overall uptime · {new Date(report.from).toLocaleDateString()} – {new Date(report.to).toLocaleDateString()}
              </span>
            </div>
            <a className={styles.downloadButton} href={slaReportHtmlUrl(buildQuery())}>
              <Download size={14} strokeWidth={1.75} />
              Download HTML
            </a>
          </div>

          <div className={styles.tableWrap}>
            <table className={styles.table}>
              <thead>
                <tr>
                  <th>Server</th>
                  <th>Group</th>
                  <th>Uptime</th>
                  <th>Downtime</th>
                  <th>Incidents</th>
                  <th>MTTR</th>
                </tr>
              </thead>
              <tbody>
                {report.servers.length === 0 ? (
                  <tr>
                    <td colSpan={6} className={styles.empty}>
                      No servers matched this period/filter
                    </td>
                  </tr>
                ) : (
                  report.servers.map((s) => (
                    <tr key={`${s.serverIp}-${s.serverName}`}>
                      <td>
                        {s.serverName}
                        {s.isRemovedFromMonitoring && <span className={styles.removedTag}> (removed)</span>}
                      </td>
                      <td className={styles.muted}>{s.serverGroup}</td>
                      <td className={styles.tabular}>{s.uptimePercent.toFixed(2)}%</td>
                      <td className={styles.tabular}>{formatTimeSpan(s.downtimeInPeriod)}</td>
                      <td className={styles.tabular}>{s.incidentCount}</td>
                      <td className={styles.tabular}>{formatTimeSpan(s.mttr)}</td>
                    </tr>
                  ))
                )}
              </tbody>
            </table>
          </div>
        </>
      )}
    </div>
  )
}
