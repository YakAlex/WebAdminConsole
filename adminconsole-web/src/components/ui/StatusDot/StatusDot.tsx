import clsx from 'clsx'
import styles from './StatusDot.module.scss'

export type StatusTone = 'success' | 'warning' | 'critical' | 'info' | 'inactive'

export interface StatusDotProps {
  tone: StatusTone
  glow?: boolean
  className?: string
}

/**
 * Small status indicator (§21: green/amber/red/cyan/gray — semantic,
 * not decorative). Used in sidebar health, Ping, Backups, RDP, Uptime
 * by Device — anywhere a status needs to be readable at a glance.
 */
export function StatusDot({ tone, glow, className }: StatusDotProps) {
  return <span className={clsx(styles.dot, styles[tone], glow && styles.glow, className)} aria-hidden="true" />
}
