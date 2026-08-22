import { ShieldAlert, TriangleAlert, ChevronRight, CircleCheck } from 'lucide-react'
import { useNavigate } from 'react-router-dom'
import clsx from 'clsx'
import { ServiceDisabledNotice } from '@/components/ui/ServiceDisabledNotice'
import styles from './AttentionRequired.module.scss'

export interface AttentionRequiredProps {
  criticalAlerts: number
  warnings: number
  /** Audit fix #4: Zabbix Monitor is disabled in Settings — the values above are already zeroed out by the caller. */
  disabled?: boolean
}

/**
 * Brief §8: card next to the Hero (~300–330px). At zero values, shows the
 * calmest possible state with an "All clear" line. If critical/warnings
 * > 0, the badge gets a colored glow rather than a bright fill.
 *
 * Data comes from useZabbixProblems() (ZabbixProblemsUpdatedOccurred): High/Disaster
 * → critical, Average/Warning → warnings.
 */
export function AttentionRequired({ criticalAlerts, warnings, disabled }: AttentionRequiredProps) {
  const navigate = useNavigate()
  const isClear = criticalAlerts === 0 && warnings === 0

  return (
    <div className={styles.card}>
      <button type="button" className={styles.header} onClick={() => navigate('/zabbix-alerts')}>
        <span className={styles.eyebrow}>Attention Required</span>
        <ChevronRight size={16} strokeWidth={1.75} className={styles.chevron} />
      </button>

      {disabled ? (
        <ServiceDisabledNotice service="Zabbix Monitor" />
      ) : (
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
      )}
    </div>
  )
}
