import { useEffect, useState } from 'react'
import { HubConnectionState } from '@microsoft/signalr'
import { LogOut } from 'lucide-react'
import { StatusDot, type StatusTone } from '@/components/ui/StatusDot'
import { useDashboardConnection } from '@/lib/signalr/DashboardConnectionContext'
import { logout } from '@/lib/api/endpoints'
import styles from './TopBar.module.scss'

const dateFormatter = new Intl.DateTimeFormat('en-US', {
  weekday: 'long',
  month: 'short',
  day: 'numeric',
  year: 'numeric',
})

const timeFormatter = new Intl.DateTimeFormat('en-US', {
  hour: '2-digit',
  minute: '2-digit',
  hour12: false,
})

const CONNECTION_LABEL: Record<HubConnectionState, string> = {
  [HubConnectionState.Connected]: 'Live',
  [HubConnectionState.Connecting]: 'Connecting…',
  [HubConnectionState.Reconnecting]: 'Reconnecting…',
  [HubConnectionState.Disconnecting]: 'Disconnecting…',
  [HubConnectionState.Disconnected]: 'Offline',
}

const CONNECTION_TONE: Record<HubConnectionState, StatusTone> = {
  [HubConnectionState.Connected]: 'success',
  [HubConnectionState.Connecting]: 'warning',
  [HubConnectionState.Reconnecting]: 'warning',
  [HubConnectionState.Disconnecting]: 'warning',
  [HubConnectionState.Disconnected]: 'critical',
}

export function TopBar() {
  const [now, setNow] = useState(() => new Date())
  const { state } = useDashboardConnection()

  useEffect(() => {
    const id = setInterval(() => setNow(new Date()), 30_000)
    return () => clearInterval(id)
  }, [])

  const handleLogout = () => {
    // A full reload after logout (rather than calling recheck()) resets
    // both the REST canary AND the SignalR connection cleanly — the same
    // reasoning as the Login page's post-login reload.
    logout().finally(() => window.location.reload())
  }

  return (
    <header className={styles.topbar}>
      <div className={styles.dateTime}>
        <span className={styles.date}>{dateFormatter.format(now)}</span>
        <span className={styles.time}>{timeFormatter.format(now)}</span>
      </div>

      <div className={styles.right}>
        <div className={styles.status}>
          <StatusDot tone={CONNECTION_TONE[state]} glow />
          <span>{CONNECTION_LABEL[state]}</span>
        </div>

        <button className={styles.logout} type="button" onClick={handleLogout} title="Вийти">
          <LogOut size={16} strokeWidth={2} />
        </button>
      </div>
    </header>
  )
}
