import { useState, type FormEvent } from 'react'
import { AlertCircle, Eye, EyeOff, Loader2, LogIn, ShieldCheck } from 'lucide-react'
import { login } from '@/lib/api/endpoints'
import { ApiError } from '@/lib/api/http'
import styles from './Login.module.scss'

/**
 * Custom login form, replacing the native Negotiate (Windows Auth) browser
 * popup. Rendered by App.tsx whenever AuthContext resolves to
 * 'unauthenticated' (a 401 from the REST canary or SignalR negotiate).
 *
 * On success this does a full `window.location.reload()` rather than
 * calling `recheck()` — the same reasoning as TopBar's logout handler
 * (TopBar.tsx): a reload resets both the REST canary AND the SignalR
 * connection cleanly. `recheck()` alone only re-runs the REST canary, so
 * DashboardConnectionProvider's SignalR connection (established once on
 * first mount, before login) would never retry and the dashboard would
 * stay stuck "Offline" until a manual reload anyway.
 */
export function Login() {
  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const [showPassword, setShowPassword] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [submitting, setSubmitting] = useState(false)

  const handleSubmit = async (event: FormEvent) => {
    event.preventDefault()
    setError(null)
    setSubmitting(true)
    try {
      await login({ username, password })
      window.location.reload()
    } catch (err: unknown) {
      setError(
        err instanceof ApiError && err.status === 429
          ? 'Забагато спроб входу. Спробуйте пізніше.'
          : "Невірне ім'я користувача або пароль.",
      )
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <div className={styles.root}>
      <form className={styles.card} onSubmit={handleSubmit} noValidate>
        <div className={styles.header}>
          <div className={styles.iconBadge}>
            <ShieldCheck size={22} strokeWidth={1.75} aria-hidden="true" />
          </div>
          <h1 className={styles.title}>Admin Console</h1>
          <p className={styles.subtitle}>Увійдіть, використовуючи обліковий запис домену</p>
        </div>

        <div className={styles.fields}>
          <label className={styles.field}>
            <span className={styles.label}>Ім'я користувача</span>
            <input
              className={styles.input}
              type="text"
              autoComplete="username"
              autoFocus
              value={username}
              onChange={(e) => setUsername(e.target.value)}
              disabled={submitting}
              required
            />
          </label>

          <label className={styles.field}>
            <span className={styles.label}>Пароль</span>
            <div className={styles.inputWrap}>
              <input
                className={styles.input}
                type={showPassword ? 'text' : 'password'}
                autoComplete="current-password"
                value={password}
                onChange={(e) => setPassword(e.target.value)}
                disabled={submitting}
                required
              />
              <button
                type="button"
                className={styles.passwordToggle}
                onClick={() => setShowPassword((v) => !v)}
                disabled={submitting}
                aria-label={showPassword ? 'Приховати пароль' : 'Показати пароль'}
              >
                {showPassword ? <EyeOff size={16} strokeWidth={1.75} /> : <Eye size={16} strokeWidth={1.75} />}
              </button>
            </div>
          </label>
        </div>

        {error && (
          <div className={styles.error} role="alert">
            <AlertCircle size={16} strokeWidth={2} className={styles.errorIcon} aria-hidden="true" />
            <span className={styles.errorText}>{error}</span>
          </div>
        )}

        <button className={styles.submit} type="submit" disabled={submitting}>
          {submitting ? (
            <Loader2 size={16} strokeWidth={2} className={styles.spinner} aria-hidden="true" />
          ) : (
            <LogIn size={16} strokeWidth={2} aria-hidden="true" />
          )}
          {submitting ? 'Вхід…' : 'Увійти'}
        </button>
      </form>
    </div>
  )
}
