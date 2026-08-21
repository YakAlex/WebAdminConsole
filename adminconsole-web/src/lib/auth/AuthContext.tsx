import { createContext, useCallback, useContext, useEffect, useRef, useState, type ReactNode } from 'react'
import { getServers } from '@/lib/api/endpoints'
import { isAuthError } from '@/lib/api/http'

export type AuthStatus = 'checking' | 'authorized' | 'denied'

interface AuthContextValue {
  status: AuthStatus
  /** Викликати з будь-якого місця (REST-хук, SignalR-провайдер), щойно прийшло 401/403. */
  reportDenied: () => void
  /** Викликати, коли якийсь канал (REST canary, SignalR negotiate) підтвердив доступ. */
  reportAuthorized: () => void
}

const AuthContext = createContext<AuthContextValue | null>(null)

/**
 * Єдине джерело правди "чи цей користувач взагалі має доступ" для всього
 * застосунку — виправлення двох проблем з фідбеку:
 *
 * 1. Flash of Unauthenticated Content: доки status === 'checking', App.tsx
 *    не рендерить ні AppLayout, ні маршрути — лише повноекранний лоадер.
 * 2. AccessDenied всередині AppLayout: тепер це ЄДИНИЙ producer стану
 *    "немає доступу" на весь застосунок (раніше кожна сторінка рахувала
 *    власний authDenied і рендерила AccessDenied як Outlet-контент,
 *    залишаючи Sidebar/TopBar видимими) — App.tsx рендерить AccessDenied
 *    ЗАМІСТЬ усього дерева маршрутів, а не всередині нього.
 *
 * Розв'язується найпершим з двох незалежних сигналів — REST canary
 * (/api/servers, нижче) або результат SignalR negotiate
 * (DashboardConnectionProvider, вкладений усередину AuthProvider, викликає
 * reportDenied/reportAuthorized) — хто відповість раніше. Після першого
 * дозволу (`resolvedRef`) статус більше не змінюється: разова перевірка,
 * а не постійний перемикач на кожен наступний запит.
 */
export function AuthProvider({ children }: { children: ReactNode }) {
  const [status, setStatus] = useState<AuthStatus>('checking')
  const resolvedRef = useRef(false)

  // reportDenied завжди спрацьовує (навіть після початкового 'authorized' —
  // напр. сесія стала недійсною пізніше) і "заморожує" resolvedRef, щоб
  // запізніла reportAuthorized з іншого каналу не відкотила denied назад.
  const reportDenied = useCallback(() => {
    resolvedRef.current = true
    setStatus('denied')
  }, [])

  // reportAuthorized розв'язує лише початкові перегони (checking → authorized)
  // — якщо стан уже вирішено (ким завгодно), повторний виклик — no-op.
  const reportAuthorized = useCallback(() => {
    if (resolvedRef.current) return
    resolvedRef.current = true
    setStatus('authorized')
  }, [])

  useEffect(() => {
    let cancelled = false

    getServers()
      .then(() => {
        if (!cancelled) reportAuthorized()
      })
      .catch((err: unknown) => {
        if (cancelled) return
        if (isAuthError(err)) {
          reportDenied()
        } else {
          // Мережева помилка/500 на canary-запиті не повинна блокувати
          // застосунок назавжди — той самий принцип, що й раніше (Крок 3):
          // блокуємо лише на підтверджений 401/403.
          reportAuthorized()
        }
      })

    return () => {
      cancelled = true
    }
  }, [reportAuthorized, reportDenied])

  return <AuthContext.Provider value={{ status, reportDenied, reportAuthorized }}>{children}</AuthContext.Provider>
}

export function useAuth(): AuthContextValue {
  const ctx = useContext(AuthContext)
  if (!ctx) throw new Error('useAuth must be used within AuthProvider')
  return ctx
}
