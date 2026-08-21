import type { HTMLAttributes, ReactNode } from 'react'
import clsx from 'clsx'
import styles from './Card.module.scss'

export interface CardProps extends HTMLAttributes<HTMLDivElement> {
  /** Прибирає внутрішній padding — для карток, що самі керують структурою (напр. таблиці на всю ширину). */
  noPadding?: boolean
  /** Трохи світліший фон — для карток, які мають візуально виділятись у сітці. */
  elevated?: boolean
  children?: ReactNode
}

/**
 * Базовий будівельний блок Design System (§19 брифу): тонкий border,
 * ледь помітний top→bottom градієнт, radius 12px, БЕЗ важкої тіні.
 * Усі майбутні картки (Ping, Uptime, Backups, RDP Sessions, ...)
 * будуються поверх цього компонента, а не дублюють CSS.
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
  /** Uppercase-заголовок картки, напр. "SYSTEM HEALTH" (§18). */
  eyebrow: ReactNode
  icon?: ReactNode
  action?: ReactNode
}

/** Стандартний header картки: іконка + uppercase-заголовок зліва, дія (напр. "View all") справа. */
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
