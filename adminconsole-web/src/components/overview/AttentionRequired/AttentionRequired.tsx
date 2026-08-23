import { ShieldAlert, TriangleAlert, Info, ChevronRight, CircleCheck } from 'lucide-react'
import { useNavigate } from 'react-router-dom'
import clsx from 'clsx'
import { ServiceDisabledNotice } from '@/components/ui/ServiceDisabledNotice'
import styles from './AttentionRequired.module.scss'

export interface AttentionRequiredProps {
  criticalAlerts: number
  warnings: number
  info: number
  /** Audit fix #4: Zabbix Monitor is disabled in Settings — the values above are already zeroed out by the caller. */
  disabled?: boolean
}

/**
 * Brief §8: card next to the Hero (~300–330px). At zero values, shows the
 * calmest possible state with an "All clear" line. If critical/warnings
 * > 0, the badge gets a colored glow rather than a bright fill.
 *
 * Data comes from useZabbixProblems() (ZabbixProblemsUpdatedOccurred): High/Disaster
 * → critical, Average/Warning → warnings, Information/NotClassified → info
 * (bug fix, 2026-08-23: "info" is reachable now that the Zabbix severity
 * threshold in Settings can go as low as 1 — this card used to drop those
 * problems on the floor and could claim "All clear" while they were active).
 *
 * Below the three counts, a composition bar shows each severity's share of
 * the current total (design review, 2026-08-24) — deliberately self-relative
 * (always sums to 100% of whatever the total happens to be right now)
 * rather than measured against an invented "normal" ceiling, since problem
 * volume can't be predicted well enough to hardcode a meaningful threshold.
 */
export function AttentionRequired({ criticalAlerts, warnings, info, disabled }: AttentionRequiredProps) {
  const navigate = useNavigate()
  const total = criticalAlerts + warnings + info
  const isClear = total === 0
  const pct = (count: number) => (count / total) * 100

  // Hide the in-bar % label once a segment is too narrow to hold it legibly.
  const MIN_LABEL_PCT = 8
  const criticalPct = Math.round(pct(criticalAlerts))
  const warningPct = Math.round(pct(warnings))
  const infoPct = Math.round(pct(info))

  return (
    <div className={styles.card}>
      <button type="button" className={styles.header} onClick={() => navigate('/zabbix-alerts')}>
        <span className={styles.eyebrow}>Zabbix Monitor</span>
        <ChevronRight size={16} strokeWidth={1.75} className={styles.chevron} />
      </button>

      {disabled ? (
        <ServiceDisabledNotice service="Zabbix Monitor" />
      ) : (
        <div className={styles.list}>
          <div className={styles.summaryRow}>
            <div className={styles.summaryItem}>
              <span className={clsx(styles.iconBadge, styles.critical, criticalAlerts > 0 && styles.attention)}>
                <ShieldAlert size={16} strokeWidth={1.75} />
              </span>
              <div className={styles.textGroup}>
                <span className={styles.count}>{criticalAlerts}</span>
                <span className={styles.label}>Critical alerts</span>
              </div>
            </div>

            <div className={styles.summaryItem}>
              <span className={clsx(styles.iconBadge, styles.warning, warnings > 0 && styles.attention)}>
                <TriangleAlert size={16} strokeWidth={1.75} />
              </span>
              <div className={styles.textGroup}>
                <span className={styles.count}>{warnings}</span>
                <span className={styles.label}>Warnings</span>
              </div>
            </div>

            <div className={styles.summaryItem}>
              <span className={clsx(styles.iconBadge, styles.info, info > 0 && styles.attention)}>
                <Info size={16} strokeWidth={1.75} />
              </span>
              <div className={styles.textGroup}>
                <span className={styles.count}>{info}</span>
                <span className={styles.label}>Informational</span>
              </div>
            </div>
          </div>

          {isClear ? (
            <div className={styles.row}>
              <span className={clsx(styles.iconBadge, styles.info)}>
                <CircleCheck size={16} strokeWidth={1.75} />
              </span>
              <div>
                <div className={styles.clearLabel}>All clear</div>
                <div className={styles.clearCaption}>No active issues</div>
              </div>
            </div>
          ) : (
            <div
              className={clsx(styles.barTrack, criticalAlerts > 0 && styles.attention)}
              role="img"
              aria-label={`Critical ${criticalAlerts}, Warnings ${warnings}, Informational ${info} — ${total} active problems total`}
            >
              <span
                className={clsx(styles.barSegment, styles.barCritical)}
                style={{ width: `${pct(criticalAlerts)}%` }}
                title={`Critical: ${criticalAlerts} (${criticalPct}%)`}
              >
                {criticalPct >= MIN_LABEL_PCT && <span className={styles.barLabel}>{criticalPct}%</span>}
              </span>
              <span
                className={clsx(styles.barSegment, styles.barWarning)}
                style={{ width: `${pct(warnings)}%` }}
                title={`Warnings: ${warnings} (${warningPct}%)`}
              >
                {warningPct >= MIN_LABEL_PCT && <span className={styles.barLabel}>{warningPct}%</span>}
              </span>
              <span
                className={clsx(styles.barSegment, styles.barInfo)}
                style={{ width: `${pct(info)}%` }}
                title={`Informational: ${info} (${infoPct}%)`}
              >
                {infoPct >= MIN_LABEL_PCT && <span className={styles.barLabel}>{infoPct}%</span>}
              </span>
            </div>
          )}
        </div>
      )}
    </div>
  )
}
