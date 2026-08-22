import { Loader2 } from 'lucide-react'
import styles from './AuthChecking.module.scss'

/**
 * Full-screen blocking state during the initial access check
 * (AuthProvider.status === 'checking') — App.tsx renders neither AppLayout
 * nor the routes until a response arrives from the REST canary or SignalR
 * negotiate. Fixes "Flash of Unauthenticated Content".
 */
export function AuthChecking() {
  return (
    <div className={styles.root}>
      <Loader2 size={28} strokeWidth={2} className={styles.spinner} />
      <span className={styles.text}>Checking access…</span>
    </div>
  )
}
