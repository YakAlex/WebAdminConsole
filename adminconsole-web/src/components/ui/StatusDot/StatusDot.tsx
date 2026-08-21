import clsx from 'clsx'
import styles from './StatusDot.module.scss'

export type StatusTone = 'success' | 'warning' | 'critical' | 'info' | 'inactive'

export interface StatusDotProps {
  tone: StatusTone
  glow?: boolean
  className?: string
}

/**
 * Маленький статус-індикатор (§21: green/amber/red/cyan/gray — semantic,
 * а не decorative). Використовується у sidebar health, Ping, Backups,
 * RDP, Uptime by Device — скрізь, де потрібен статус одного погляду.
 */
export function StatusDot({ tone, glow, className }: StatusDotProps) {
  return <span className={clsx(styles.dot, styles[tone], glow && styles.glow, className)} aria-hidden="true" />
}
