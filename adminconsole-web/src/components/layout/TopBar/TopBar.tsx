import { useEffect, useState } from 'react'
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

/**
 * Глобальна верхня панель (§3 брифу). Крок 1 UX-polish (2026-08-21):
 * прибрано непрацюючі елементи (Search, "Updated just now"+refresh,
 * avatar) — лишається лише дата/час і статус системи.
 */
export function TopBar() {
  const [now, setNow] = useState(() => new Date())

  useEffect(() => {
    const id = setInterval(() => setNow(new Date()), 30_000)
    return () => clearInterval(id)
  }, [])

  return (
    <header className={styles.topbar}>
      <div className={styles.right}>
        <div className={styles.dateTime}>
          <span className={styles.date}>{dateFormatter.format(now)}</span>
          <span className={styles.time}>{timeFormatter.format(now)}</span>
        </div>

        <div className={styles.status}>
          <span className={styles.statusDot} aria-hidden="true" />
          <span>All systems operational</span>
        </div>
      </div>
    </header>
  )
}
