import { User } from 'lucide-react'
import clsx from 'clsx'
import { StatusDot, type StatusTone } from '@/components/ui/StatusDot'
import { RdpSessionState, type RdpSessionInfo } from '@/lib/api/types'
import styles from './RdpSessionsTable.module.scss'

const STATE_LABEL: Record<RdpSessionState, string> = {
  [RdpSessionState.Active]: 'Active',
  [RdpSessionState.Idle]: 'Idle',
  [RdpSessionState.Disconnected]: 'Disconnected',
  [RdpSessionState.Unknown]: 'Unknown',
}

const STATE_TONE: Record<RdpSessionState, StatusTone> = {
  [RdpSessionState.Active]: 'success',
  [RdpSessionState.Idle]: 'warning',
  [RdpSessionState.Disconnected]: 'inactive',
  [RdpSessionState.Unknown]: 'inactive',
}

const STATE_TEXT_CLASS: Record<StatusTone, string> = {
  success: 'statusSuccess',
  warning: 'statusWarning',
  critical: 'statusWarning',
  inactive: 'statusInactive',
  info: 'statusInactive',
}

export interface RdpSessionsTableProps {
  sessions: RdpSessionInfo[]
}

// Крок 3 (#4): активні сесії — зверху, відключені — знизу (в межах групи —
// за іменем користувача), щоб "хто зараз підключений" читалось одразу,
// не гортаючи весь список.
const STATE_ORDER: Record<RdpSessionState, number> = {
  [RdpSessionState.Active]: 0,
  [RdpSessionState.Idle]: 1,
  [RdpSessionState.Disconnected]: 2,
  [RdpSessionState.Unknown]: 3,
}

/** §26 брифу (RDP): "Connected users" / детальна таблиця сесій. Дані — useRdpSessions() (SignalR, без REST-знімка). */
export function RdpSessionsTable({ sessions }: RdpSessionsTableProps) {
  const sortedSessions = [...sessions].sort((a, b) => {
    const stateDiff = STATE_ORDER[a.state] - STATE_ORDER[b.state]
    return stateDiff !== 0 ? stateDiff : a.username.localeCompare(b.username)
  })

  return (
    <div className={styles.card}>
      <div className={styles.header}>
        <span className={styles.eyebrow}>Sessions</span>
      </div>

      {sessions.length === 0 ? (
        <div className={styles.empty}>No active RDP sessions</div>
      ) : (
        <div className={styles.tableWrap}>
          <table className={styles.table}>
            <thead>
              <tr>
                <th>User</th>
                <th>Server</th>
                <th>State</th>
                <th>Session</th>
                <th>Idle time</th>
                <th>Logon time</th>
              </tr>
            </thead>
            <tbody>
              {sortedSessions.map((session) => (
                <tr key={`${session.serverName}-${session.sessionId}`}>
                  <td>
                    <span className={styles.user}>
                      <User size={14} strokeWidth={1.75} className={styles.userIcon} />
                      {session.username}
                    </span>
                  </td>
                  <td className={styles.server}>{session.serverName}</td>
                  <td>
                    <span className={clsx(styles.status, styles[STATE_TEXT_CLASS[STATE_TONE[session.state]]])}>
                      <StatusDot tone={STATE_TONE[session.state]} />
                      {STATE_LABEL[session.state]}
                    </span>
                  </td>
                  <td className={styles.session}>{session.sessionName}</td>
                  <td className={styles.idle}>{session.idleTime}</td>
                  <td className={styles.logon}>{session.logonTime}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  )
}
