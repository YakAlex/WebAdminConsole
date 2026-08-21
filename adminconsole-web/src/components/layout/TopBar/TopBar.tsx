import { useEffect, useState } from 'react'
import { HubConnectionState } from '@microsoft/signalr'
import { StatusDot, type StatusTone } from '@/components/ui/StatusDot'
import { useDashboardConnection } from '@/lib/signalr/DashboardConnectionContext'
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

/**
 * Глобальна верхня панель (§3 брифу). Крок 1 UX-polish (2026-08-21):
 * прибрано непрацюючі елементи (Search, "Updated just now"+refresh,
 * avatar) — лишається лише дата/час і статус системи.
 *
 * UX-фікс (2026-08-22): дата/час — тепер лівий край (замість правого),
 * "All systems operational" (раніше — статичний, нічим не підкріплений
 * напис, завжди зелений незалежно від реального стану) замінено на
 * СПРАВЖНІй live-індикатор SignalR-з'єднання (useDashboardConnection) —
 * правий край не порожній, і напис тепер каже правду про стан застосунку.
 */
export function TopBar() {
  const [now, setNow] = useState(() => new Date())
  const { state } = useDashboardConnection()

  useEffect(() => {
    const id = setInterval(() => setNow(new Date()), 30_000)
    return () => clearInterval(id)
  }, [])

  return (
    <header className={styles.topbar}>
      <div className={styles.dateTime}>
        <span className={styles.date}>{dateFormatter.format(now)}</span>
        <span className={styles.time}>{timeFormatter.format(now)}</span>
      </div>

      <div className={styles.status}>
        <StatusDot tone={CONNECTION_TONE[state]} glow />
        <span>{CONNECTION_LABEL[state]}</span>
      </div>
    </header>
  )
}
