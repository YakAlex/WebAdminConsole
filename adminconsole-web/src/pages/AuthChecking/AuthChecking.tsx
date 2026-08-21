import { Loader2 } from 'lucide-react'
import styles from './AuthChecking.module.scss'

/**
 * Повноекранний блокуючий стан на час первинної перевірки доступу
 * (AuthProvider.status === 'checking') — App.tsx не рендерить ні AppLayout,
 * ні маршрути, доки не прийде відповідь від REST canary або SignalR
 * negotiate. Виправляє "Flash of Unauthenticated Content".
 */
export function AuthChecking() {
  return (
    <div className={styles.root}>
      <Loader2 size={28} strokeWidth={2} className={styles.spinner} />
      <span className={styles.text}>Checking access…</span>
    </div>
  )
}
