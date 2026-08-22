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
 * Аудит Зона 6, Знахідка №2 (2026-08-22): раніше жодного React Error
 * Boundary не було в усьому застосунку (підтверджено грепом) — будь-яка
 * непіймана помилка рендеру на БУДЬ-ЯКІЙ сторінці клала БІЛИЙ ЕКРАН усього
 * застосунку замість ізольованого, зрозумілого фолбеку. Class-компонент —
 * componentDidCatch/getDerivedStateFromError не мають хук-еквівалента.
 *
 * Розміщено в main.tsx навколо всього дерева (AuthProvider +
 * DashboardConnectionProvider + App) — єдина глобальна страхувальна сітка;
 * per-page межі можна додати пізніше, якщо знадобиться ізоляція точніше.
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
