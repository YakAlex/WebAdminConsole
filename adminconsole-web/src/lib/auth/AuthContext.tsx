import { createContext, useCallback, useContext, useEffect, useRef, useState, type ReactNode } from 'react'
import { getServers } from '@/lib/api/endpoints'
import { isAuthError } from '@/lib/api/http'

export type AuthStatus = 'checking' | 'authorized' | 'denied'

interface AuthContextValue {
  status: AuthStatus
  /** Call from anywhere (a REST hook, the SignalR provider) as soon as a 401/403 comes in. */
  reportDenied: () => void
  /** Call when some channel (REST canary, SignalR negotiate) has confirmed access. */
  reportAuthorized: () => void
}

const AuthContext = createContext<AuthContextValue | null>(null)

/**
 * The single source of truth for "does this user have access at all"
 * for the whole app — fixes two issues from feedback:
 *
 * 1. Flash of Unauthenticated Content: while status === 'checking',
 *    App.tsx renders neither AppLayout nor any routes — just a
 *    full-screen loader.
 * 2. AccessDenied inside AppLayout: this is now the ONE producer of
 *    "no access" state for the whole app (previously every page
 *    tracked its own authDenied and rendered AccessDenied as Outlet
 *    content, leaving the Sidebar/TopBar visible) — App.tsx renders
 *    AccessDenied INSTEAD OF the whole route tree, not inside it.
 *
 * Resolved by whichever of two independent signals answers first — a
 * REST canary (/api/servers, below) or the result of the SignalR
 * negotiate (DashboardConnectionProvider, nested inside AuthProvider,
 * calls reportDenied/reportAuthorized). After the first resolution
 * (`resolvedRef`), the status no longer changes: it's a one-time
 * check, not a permanent toggle on every subsequent request.
 */
export function AuthProvider({ children }: { children: ReactNode }) {
  const [status, setStatus] = useState<AuthStatus>('checking')
  const resolvedRef = useRef(false)

  // reportDenied always fires (even after an initial 'authorized' —
  // e.g. the session became invalid later) and "freezes" resolvedRef
  // so a late reportAuthorized from another channel can't roll denied
  // back.
  const reportDenied = useCallback(() => {
    resolvedRef.current = true
    setStatus('denied')
  }, [])

  // reportAuthorized only resolves the initial race (checking →
  // authorized) — if the state is already resolved (by anyone), a
  // repeat call is a no-op.
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
          // A network error/500 on the canary request shouldn't block
          // the app forever — the same principle as before (Step 3):
          // only block on a confirmed 401/403.
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
