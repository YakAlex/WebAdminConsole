import { PowerOff } from 'lucide-react'
import styles from './ServiceDisabledNotice.module.scss'

export interface ServiceDisabledNoticeProps {
  /** Назва сервісу для повідомлення, напр. "Zabbix Monitor", "RDP Monitor". */
  service: string
}

/**
 * Аудит-фікс п.4: показується ЗАМІСТЬ (можливо застарілих) даних сервісу,
 * вимкненого в Settings → Monitoring Services — див. useMonitoringToggles().
 */
export function ServiceDisabledNotice({ service }: ServiceDisabledNoticeProps) {
  return (
    <div className={styles.banner}>
      <PowerOff size={14} strokeWidth={1.75} className={styles.icon} />
      <span>{service}: цей сервіс наразі вимкнено в налаштуваннях.</span>
    </div>
  )
}
