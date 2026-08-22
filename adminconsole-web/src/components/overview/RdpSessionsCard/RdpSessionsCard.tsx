import { MonitorX, SquareTerminal } from 'lucide-react'
import { useNavigate } from 'react-router-dom'
import { ServiceDisabledNotice } from '@/components/ui/ServiceDisabledNotice'
import { StatusDot } from '@/components/ui/StatusDot'
import { formatDuration } from '@/lib/format'
import type { LastLogout } from '@/hooks/dashboard/useRdpSessions'
import type { RdpSessionInfo } from '@/lib/api/types'
import styles from './RdpSessionsCard.module.scss'

export interface RdpSessionsCardProps {
  /** Already filtered to Active-only (useOverviewViewModel, Step 3 #4). */
  sessions: RdpSessionInfo[]
  /** Last recorded disconnect (from any server) — shown when there are no active sessions. */
  lastLogout?: LastLogout | null
  /** Audit fix #4: RDP Monitor is disabled in Settings — sessions is already zeroed out by the caller. */
  disabled?: boolean
}

/** Brief §14: adapts based on whether there are active sessions. Data comes from useRdpSessions() (SignalR, no REST snapshot). */
export function RdpSessionsCard({ sessions, lastLogout, disabled }: RdpSessionsCardProps) {
  const navigate = useNavigate()

  return (
    <div className={styles.card}>
      <div className={styles.header}>
        <span className={styles.eyebrow}>
          <SquareTerminal size={14} strokeWidth={1.75} />
          RDP Sessions
        </span>
        <button type="button" className={styles.headerLink} onClick={() => navigate('/rdp-sessions')}>
          View all
        </button>
      </div>

      {disabled ? (
        <ServiceDisabledNotice service="RDP Monitor" />
      ) : (
        <>
          <div className={styles.headline}>
            <span className={styles.count}>{sessions.length}</span>
            <span className={styles.countLabel}>Active sessions</span>
          </div>

          <div className={styles.divider} />

          {sessions.length === 0 ? (
            lastLogout ? (
              <div className={styles.lastSession}>
                <span className={styles.lastSessionLabel}>No active sessions — last one</span>
                <div className={styles.session}>
                  <div>
                    <div className={styles.sessionUser}>
                      <StatusDot tone="inactive" className={styles.sessionDot} />
                      {lastLogout.username}
                    </div>
                    <div className={styles.sessionMeta}>{lastLogout.serverName} · Disconnected</div>
                  </div>
                  <span className={styles.sessionDuration}>{formatDuration(lastLogout.at, null)} ago</span>
                </div>
              </div>
            ) : (
              <div className={styles.empty}>
                <MonitorX size={18} strokeWidth={1.5} className={styles.emptyIcon} />
                <span className={styles.emptyText}>No active RDP sessions</span>
                <span className={styles.emptyClear}>All clear</span>
              </div>
            )
          ) : (
            <div className={styles.sessions}>
              {sessions.map((session) => (
                <div className={styles.session} key={`${session.serverName}-${session.sessionId}`}>
                  <div>
                    <div className={styles.sessionUser}>{session.serverName}</div>
                    <div className={styles.sessionMeta}>{session.username}</div>
                  </div>
                  <span className={styles.sessionDuration}>{session.idleTime}</span>
                </div>
              ))}
            </div>
          )}
        </>
      )}
    </div>
  )
}
