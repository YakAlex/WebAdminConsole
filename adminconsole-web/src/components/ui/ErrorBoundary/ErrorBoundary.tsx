import { Component, type ErrorInfo, type ReactNode } from 'react'
import { TriangleAlert } from 'lucide-react'
import styles from './ErrorBoundary.module.scss'

export interface ErrorBoundaryProps {
  children: ReactNode
}

interface ErrorBoundaryState {
  error: Error | null
}

/**
 * Audit Zone 6, Finding #2 (2026-08-22): previously the app had no React
 * Error Boundary at all (confirmed via grep) — any uncaught render error on
 * ANY page took down the ENTIRE app with a BLANK WHITE SCREEN instead of an
 * isolated, understandable fallback. Class component — componentDidCatch /
 * getDerivedStateFromError have no hook equivalent.
 *
 * Placed in main.tsx around the whole tree (AuthProvider +
 * DashboardConnectionProvider + App) as a single global safety net;
 * per-page boundaries can be added later if finer isolation is needed.
 */
export class ErrorBoundary extends Component<ErrorBoundaryProps, ErrorBoundaryState> {
  state: ErrorBoundaryState = { error: null }

  static getDerivedStateFromError(error: Error): ErrorBoundaryState {
    return { error }
  }

  componentDidCatch(error: Error, info: ErrorInfo) {
    console.error('[ErrorBoundary] Unhandled render error:', error, info.componentStack)
  }

  render() {
    if (!this.state.error) return this.props.children

    return (
      <div className={styles.wrap}>
        <div className={styles.card}>
          <TriangleAlert size={28} strokeWidth={1.75} className={styles.icon} />
          <h1 className={styles.title}>Something went wrong</h1>
          <p className={styles.message}>
            The app hit an unexpected error and couldn't continue rendering this page. Reloading usually fixes it.
          </p>
          <pre className={styles.detail}>{this.state.error.message}</pre>
          <button type="button" className={styles.reloadButton} onClick={() => window.location.reload()}>
            Reload page
          </button>
        </div>
      </div>
    )
  }
}
