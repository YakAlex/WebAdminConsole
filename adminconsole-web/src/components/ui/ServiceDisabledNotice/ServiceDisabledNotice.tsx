import { PowerOff } from 'lucide-react'
import styles from './ServiceDisabledNotice.module.scss'

export interface ServiceDisabledNoticeProps {
  /** Service name for the message, e.g. "Zabbix Monitor", "RDP Monitor". */
  service: string
}

/**
 * Audit fix item 4: shown INSTEAD OF (potentially stale) service data
 * when the service is disabled in Settings → Monitoring Services — see
 * useMonitoringToggles().
 */
export function ServiceDisabledNotice({ service }: ServiceDisabledNoticeProps) {
  return (
    <div className={styles.banner}>
      <PowerOff size={14} strokeWidth={1.75} className={styles.icon} />
      <span>{service}: this service is currently disabled in settings.</span>
    </div>
  )
}
