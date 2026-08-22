import { ServerCrash } from 'lucide-react'
import { StatusDot } from '@/components/ui/StatusDot'
import { ZabbixSeverity, type ZabbixProblem } from '@/lib/api/types'
import { formatClockWithSeconds } from '@/lib/format'
import styles from './ZabbixProblemsTable.module.scss'

const SEVERITY_LABEL: Record<ZabbixSeverity, string> = {
  [ZabbixSeverity.NotClassified]: 'Not classified',
  [ZabbixSeverity.Information]: 'Information',
  [ZabbixSeverity.Warning]: 'Warning',
  [ZabbixSeverity.Average]: 'Average',
  [ZabbixSeverity.High]: 'High',
  [ZabbixSeverity.Disaster]: 'Disaster',
}

const SEVERITY_CLASS: Record<ZabbixSeverity, string> = {
  [ZabbixSeverity.NotClassified]: 'severityInfo',
  [ZabbixSeverity.Information]: 'severityInfo',
  [ZabbixSeverity.Warning]: 'severityWarning',
  [ZabbixSeverity.Average]: 'severityAverage',
  [ZabbixSeverity.High]: 'severityHigh',
  [ZabbixSeverity.Disaster]: 'severityDisaster',
}

const SEVERITY_DOT_TONE = {
  [ZabbixSeverity.NotClassified]: 'inactive',
  [ZabbixSeverity.Information]: 'info',
  [ZabbixSeverity.Warning]: 'warning',
  [ZabbixSeverity.Average]: 'warning',
  [ZabbixSeverity.High]: 'critical',
  [ZabbixSeverity.Disaster]: 'critical',
} as const

export interface ZabbixProblemsTableProps {
  problems: ZabbixProblem[]
}

/** Brief §26 (Zabbix): "Active alerts" — detailed table. Data — useZabbixProblems() (SignalR, no REST snapshot). */
export function ZabbixProblemsTable({ problems }: ZabbixProblemsTableProps) {
  return (
    <div className={styles.card}>
      <div className={styles.header}>
        <span className={styles.eyebrow}>Active Problems</span>
      </div>

      {problems.length === 0 ? (
        <div className={styles.empty}>No active problems</div>
      ) : (
        <div className={styles.tableWrap}>
          <table className={styles.table}>
            <thead>
              <tr>
                <th>Host</th>
                <th>Description</th>
                <th>Severity</th>
                <th>Age</th>
                <th>Started</th>
              </tr>
            </thead>
            <tbody>
              {problems.map((problem) => (
                <tr key={problem.eventId}>
                  <td>
                    <span className={styles.host}>
                      <ServerCrash size={14} strokeWidth={1.75} className={styles.hostIcon} />
                      {problem.hostName}
                    </span>
                  </td>
                  <td className={styles.description}>{problem.description}</td>
                  <td>
                    <span className={`${styles.severity} ${styles[SEVERITY_CLASS[problem.severity]]}`}>
                      <StatusDot tone={SEVERITY_DOT_TONE[problem.severity]} />
                      {SEVERITY_LABEL[problem.severity]}
                    </span>
                  </td>
                  <td className={styles.age}>{problem.ageDisplay}</td>
                  <td className={styles.started}>{formatClockWithSeconds(problem.startTime)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  )
}
