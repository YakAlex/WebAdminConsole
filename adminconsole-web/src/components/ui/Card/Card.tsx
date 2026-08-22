import type { HTMLAttributes, ReactNode } from 'react'
import clsx from 'clsx'
import styles from './Card.module.scss'

export interface CardProps extends HTMLAttributes<HTMLDivElement> {
  /** Removes inner padding — for cards that manage their own structure (e.g. a full-width table). */
  noPadding?: boolean
  /** A slightly lighter background — for cards that should visually stand out in the grid. */
  elevated?: boolean
  children?: ReactNode
}

/**
 * Base building block of the Design System (§19 of the brief): thin
 * border, a barely-visible top→bottom gradient, 12px radius, NO heavy
 * shadow. All future cards (Ping, Uptime, Backups, RDP Sessions, ...)
 * are built on top of this component instead of duplicating CSS.
 */
export function Card({ noPadding, elevated, className, children, ...rest }: CardProps) {
  return (
    <div
      className={clsx(styles.card, noPadding && styles.noPadding, elevated && styles.elevated, className)}
      {...rest}
    >
      {children}
    </div>
  )
}

export interface CardHeaderProps {
  /** Uppercase card title, e.g. "SYSTEM HEALTH" (§18). */
  eyebrow: ReactNode
  icon?: ReactNode
  action?: ReactNode
}

/** Standard card header: icon + uppercase title on the left, an action (e.g. "View all") on the right. */
export function CardHeader({ eyebrow, icon, action }: CardHeaderProps) {
  return (
    <div className={styles.header}>
      <div className={styles.eyebrow}>
        {icon}
        <span>{eyebrow}</span>
      </div>
      {action}
    </div>
  )
}
