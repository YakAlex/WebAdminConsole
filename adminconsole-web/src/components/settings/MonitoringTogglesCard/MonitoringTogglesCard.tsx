import { Activity } from 'lucide-react'
import { Card, CardHeader } from '@/components/ui/Card'
import { ToggleSwitch } from '@/components/ui/ToggleSwitch'
import styles from './MonitoringTogglesCard.module.scss'

export type MonitoredServiceKey = 'zabbix' | 'rdp' | 'backup'

export interface MonitoringTogglesCardProps {
  zabbixEnabled: boolean
  rdpEnabled: boolean
  backupEnabled: boolean
  onToggle: (service: MonitoredServiceKey, enabled: boolean) => void
  saving: boolean
}

const ROWS: { key: MonitoredServiceKey; label: string; hint: string }[] = [
  { key: 'zabbix', label: 'Zabbix Monitor', hint: 'Polls active problems from Zabbix.' },
  { key: 'rdp', label: 'RDP Monitor', hint: 'Polls terminal servers for active RDP sessions.' },
  { key: 'backup', label: 'Backup Monitor', hint: 'Checks configured backup jobs on schedule.' },
]

/**
 * Крок 4 (#7): вмикачі фонових сервісів — точний аналог WPF
 * RdpMonitoringEnabled/ZabbixMonitoringEnabled/BackupMonitoringEnabled з
 * UserSettings. Зміна діє негайно (бекенд прокидає відповідний поллер),
 * без рестарту служби.
 */
export function MonitoringTogglesCard({
  zabbixEnabled,
  rdpEnabled,
  backupEnabled,
  onToggle,
  saving,
}: MonitoringTogglesCardProps) {
  const enabled: Record<MonitoredServiceKey, boolean> = {
    zabbix: zabbixEnabled,
    rdp: rdpEnabled,
    backup: backupEnabled,
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
      </div>
    </Card>
  )
}
