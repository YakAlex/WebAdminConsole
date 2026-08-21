import { ShieldAlert, TriangleAlert, ChevronRight, CircleCheck } from 'lucide-react'
import { useNavigate } from 'react-router-dom'
import clsx from 'clsx'
import styles from './AttentionRequired.module.scss'

export interface AttentionRequiredProps {
  criticalAlerts: number
  warnings: number
}

/**
 * §8 брифу: картка поруч із Hero (~300–330px). При нульових значеннях —
 * максимально спокійний стан із рядком "All clear". Якщо critical/warnings
 * > 0, badge отримує колірний glow, а не яскраву заливку.
 *
 * Дані — з useZabbixProblems() (ZabbixProblemsUpdatedOccurred): High/Disaster
 * → critical, Average/Warning → warnings.
 */
export function AttentionRequired({ criticalAlerts, warnings }: AttentionRequiredProps) {
  const navigate = useNavigate()
  const isClear = criticalAlerts === 0 && warnings === 0

  return (
    <div className={styles.card}>
      <button type="button" className={styles.header} onClick={() => navigate('/zabbix-alerts')}>
        <span className={styles.eyebrow}>Attention Required</span>
        <ChevronRight size={16} strokeWidth={1.75} className={styles.chevron} />
      </button>

      <div className={styles.list}>
        <div className={styles.row}>
          <span className={clsx(styles.iconBadge, styles.critical, criticalAlerts > 0 && styles.attention)}>
            <ShieldAlert size={16} strokeWidth={1.75} />
          </span>
          <span className={styles.count}>{criticalAlerts}</span>
          <span className={styles.label}>Critical alerts</span>
        </div>

        <div className={styles.row}>
          <span className={clsx(styles.iconBadge, styles.warning, warnings > 0 && styles.attention)}>
            <TriangleAlert size={16} strokeWidth={1.75} />
          </span>
          <span className={styles.count}>{warnings}</span>
          <span className={styles.label}>Warnings</span>
        </div>

        {isClear && (
          <div className={styles.row}>
            <span className={clsx(styles.iconBadge, styles.info)}>
              <CircleCheck size={16} strokeWidth={1.75} />
            </span>
            <div>
              <div className={styles.clearLabel}>All clear</div>
              <div className={styles.clearCaption}>No active issues</div>
            </div>
          </div>
        )}
      </div>
    </div>
  )
}
