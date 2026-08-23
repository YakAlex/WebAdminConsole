import { useEffect, useState } from 'react'
import { Activity } from 'lucide-react'
import clsx from 'clsx'
import { Card, CardHeader } from '@/components/ui/Card'
import { ToggleSwitch } from '@/components/ui/ToggleSwitch'
import styles from './MonitoringTogglesCard.module.scss'

export type MonitoredServiceKey = 'zabbix' | 'rdp' | 'backup'

export interface MonitoringTogglesCardProps {
  zabbixEnabled: boolean
  rdpEnabled: boolean
  backupEnabled: boolean
  zabbixMinSeverity: number
  onToggle: (service: MonitoredServiceKey, enabled: boolean) => void
  /** Resolves false on a failed save — the caller reverts its optimistic slider position when it does. */
  onZabbixMinSeverityChange: (minSeverity: number) => Promise<boolean>
  saving: boolean
}

const SEVERITY_LEVELS = [
  { value: 1, label: 'Information' },
  { value: 2, label: 'Warning' },
  { value: 3, label: 'Average' },
  { value: 4, label: 'High' },
  { value: 5, label: 'Disaster' },
]

/** Defends against a stored ZabbixMinSeverity outside [1,5] (e.g. a pre-validation-fix DB row) — SEVERITY_LEVELS has no entry for 0 or 6+. */
function severityLevel(value: number) {
  const clamped = Math.min(Math.max(Math.round(value), 1), 5)
  return SEVERITY_LEVELS[clamped - 1]
}

const ROWS: { key: MonitoredServiceKey; label: string; hint: string }[] = [
  { key: 'zabbix', label: 'Zabbix Monitor', hint: 'Polls active problems from Zabbix.' },
  { key: 'rdp', label: 'RDP Monitor', hint: 'Polls terminal servers for active RDP sessions.' },
  { key: 'backup', label: 'Backup Monitor', hint: 'Checks configured backup jobs on schedule.' },
]

/**
 * Step 4 (#7): background service toggles — a direct analog of the WPF
 * RdpMonitoringEnabled/ZabbixMonitoringEnabled/BackupMonitoringEnabled
 * from UserSettings. Changes take effect immediately (the backend signals
 * the corresponding poller), no service restart needed.
 */
export function MonitoringTogglesCard({
  zabbixEnabled,
  rdpEnabled,
  backupEnabled,
  zabbixMinSeverity,
  onToggle,
  onZabbixMinSeverityChange,
  saving,
}: MonitoringTogglesCardProps) {
  const enabled: Record<MonitoredServiceKey, boolean> = {
    zabbix: zabbixEnabled,
    rdp: rdpEnabled,
    backup: backupEnabled,
  }

  // Live-drags the thumb/labels locally; only calls onZabbixMinSeverityChange
  // (PUT + poller wake-up) once the user releases — dragging fires the native
  // `input` event (React's onChange) on every pixel, and committing on each
  // of those would flood the API with a PUT per step. onWheel commits too
  // (a focused range input changes value on mouse-wheel scroll in some
  // browsers, notably Firefox, with no mouseup/keyup to catch it otherwise).
  // onTouchCancel (an interrupted touch gesture — OS gesture conflict,
  // notification pull-down) snaps the thumb back instead of leaving an
  // uncommitted value on screen with nothing to un-stick it.
  const [liveSeverity, setLiveSeverity] = useState(zabbixMinSeverity)
  useEffect(() => setLiveSeverity(zabbixMinSeverity), [zabbixMinSeverity])

  const commitSeverity = async (e: React.SyntheticEvent<HTMLInputElement>) => {
    const next = Number(e.currentTarget.value)
    if (next === zabbixMinSeverity) return
    const ok = await onZabbixMinSeverityChange(next)
    if (!ok) setLiveSeverity(zabbixMinSeverity) // save failed — snap the thumb back to the last-known-good value
  }

  return (
    <Card>
      <CardHeader eyebrow="Monitoring Services" icon={<Activity size={14} strokeWidth={1.75} />} />
      <span className={styles.description}>
        Enable or disable background monitoring services. Takes effect immediately, no restart needed.
      </span>

      <div className={styles.rows}>
        {ROWS.map((row) => (
          <div className={styles.row} key={row.key}>
            <div className={styles.text}>
              <span className={styles.label}>{row.label}</span>
              <span className={styles.hint}>{row.hint}</span>
            </div>
            <ToggleSwitch
              checked={enabled[row.key]}
              onChange={(value) => onToggle(row.key, value)}
              disabled={saving}
              ariaLabel={`Toggle ${row.label}`}
            />
          </div>
        ))}

        <div className={styles.severityRow}>
          <div className={styles.text}>
            <span className={styles.label}>Zabbix alert threshold</span>
            <span className={styles.hint}>
              Pulls in {severityLevel(liveSeverity).label.toLowerCase()}-severity problems and above.
            </span>
          </div>

          <div className={styles.slider}>
            <input
              type="range"
              min={1}
              max={5}
              step={1}
              value={liveSeverity}
              onChange={(e) => setLiveSeverity(Number(e.target.value))}
              onMouseUp={commitSeverity}
              onTouchEnd={commitSeverity}
              onTouchCancel={() => setLiveSeverity(zabbixMinSeverity)}
              onKeyUp={commitSeverity}
              onWheel={commitSeverity}
              disabled={saving}
              aria-label="Zabbix alert threshold"
              className={styles.sliderInput}
            />
            <div className={styles.sliderTicks}>
              {SEVERITY_LEVELS.map((level) => (
                <span
                  key={level.value}
                  className={clsx(styles.sliderTick, level.value === liveSeverity && styles.sliderTickActive)}
                >
                  {level.value}
                  <em>{level.label}</em>
                </span>
              ))}
            </div>
          </div>
        </div>
      </div>
    </Card>
  )
}
