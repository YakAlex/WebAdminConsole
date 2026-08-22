import type { ReactNode } from 'react'
import styles from './PageHeader.module.scss'

export interface PageHeaderProps {
  title: string
  subtitle?: string
  actions?: ReactNode
}

/**
 * §6 of the brief, generalized for any tab (§26/§27 — a shared page
 * header component instead of repeating markup in every Page).
 * Date/time and the greeting stay exclusive to Overview — this is just
 * the title + optional subtitle/actions.
 */
export function PageHeader({ title, subtitle, actions }: PageHeaderProps) {
  return (
    <div className={styles.root}>
      <div>
        <h1 className={styles.title}>{title}</h1>
        {subtitle && <p className={styles.subtitle}>{subtitle}</p>}
      </div>
      {actions && <div className={styles.actions}>{actions}</div>}
    </div>
  )
}
