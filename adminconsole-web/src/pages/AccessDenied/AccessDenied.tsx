import { ShieldOff } from 'lucide-react'
import styles from './AccessDenied.module.scss'

/** T6.2 §3: shown instead of the dashboard when REST/SignalR return 401/403. */
export function AccessDenied() {
  return (
    <div className={styles.root}>
      <ShieldOff size={40} strokeWidth={1.5} className={styles.icon} />
      <h1 className={styles.title}>Access Denied</h1>
      <p className={styles.text}>
        Your account is not a member of the group permitted to view Admin Console. Please contact your
        administrator to request access.
      </p>
      <span className={styles.code}>HTTP 401 / 403</span>
    </div>
  )
}
