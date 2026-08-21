import { ShieldOff } from 'lucide-react'
import styles from './AccessDenied.module.scss'

/** T6.2 п.3: показується замість дашборду, коли REST/SignalR повертають 401/403. */
export function AccessDenied() {
  return (
    <div className={styles.root}>
      <ShieldOff size={40} strokeWidth={1.5} className={styles.icon} />
      <h1 className={styles.title}>Access Denied</h1>
      <p className={styles.text}>
        Ваш обліковий запис не входить до групи, якій дозволено переглядати Admin Console. Зверніться до
        адміністратора для отримання доступу.
      </p>
      <span className={styles.code}>HTTP 401 / 403</span>
    </div>
  )
}
