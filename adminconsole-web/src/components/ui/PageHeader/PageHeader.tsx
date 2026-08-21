import type { ReactNode } from 'react'
import styles from './PageHeader.module.scss'

export interface PageHeaderProps {
  title: string
  subtitle?: string
  actions?: ReactNode
}

/**
 * §6 брифу, узагальнено для будь-якої вкладки (§26/§27 — спільний
 * компонент шапки сторінки замість повторення розмітки в кожному Page).
 * Дата/час і привітання лишились винятково на Overview — тут лише
 * заголовок + опційний підзаголовок/дії.
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
