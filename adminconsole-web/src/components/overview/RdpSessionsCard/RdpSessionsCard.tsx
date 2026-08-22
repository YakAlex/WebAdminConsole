import { MonitorX, SquareTerminal } from 'lucide-react'
import { useNavigate } from 'react-router-dom'
import { ServiceDisabledNotice } from '@/components/ui/ServiceDisabledNotice'
import { StatusDot } from '@/components/ui/StatusDot'
import { formatDuration } from '@/lib/format'
import type { LastLogout } from '@/hooks/dashboard/useRdpSessions'
import type { RdpSessionInfo } from '@/lib/api/types'
import styles from './RdpSessionsCard.module.scss'

export interface RdpSessionsCardProps {
  /** Уже відфільтровано на Active-only (useOverviewViewModel, Крок 3 #4). */
  sessions: RdpSessionInfo[]
  /** Останній зафіксований disconnect (будь-якого сервера) — показуємо, коли активних сесій нема. */
  lastLogout?: LastLogout | null
  /** Аудит-фікс п.4: RDP Monitor вимкнено в Settings — sessions вже занулено викликачем. */
  disabled?: boolean
}

/** §14 брифу: адаптується під наявність активних сесій. Дані — з useRdpSessions() (SignalR, без REST-знімка). */
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
