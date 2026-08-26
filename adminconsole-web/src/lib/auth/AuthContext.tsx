import { createContext, useCallback, useContext, useEffect, useRef, useState, type ReactNode } from 'react'
import { getServers } from '@/lib/api/endpoints'
import { isAuthError } from '@/lib/api/http'

export type AuthStatus = 'checking' | 'unauthenticated' | 'authorized' | 'denied'

interface AuthContextValue {
  status: AuthStatus
  /** Call from anywhere (a REST hook, the SignalR provider) as soon as a 403 comes in (authenticated but not in the AD group). */
  reportDenied: () => void
  /** Call as soon as a 401 comes in (no valid session — show the login page). */
  reportUnauthenticated: () => void
  /** Call when some channel (REST canary, SignalR negotiate) has confirmed access. */
  reportAuthorized: () => void
  /** Re-runs the REST canary — call after a successful login. */
  recheck: () => void
}

const AuthContext = createContext<AuthContextValue | null>(null)

/**
 * The single source of truth for "does this user have access at all" for
 * the whole app.
 *
 * Resolved by whichever of two independent signals answers first — a REST
 * canary (/api/servers, below) or the result of the SignalR negotiate
 * (DashboardConnectionProvider, nested inside AuthProvider, calls
 * reportDenied/reportUnauthenticated/reportAuthorized). After the first
 * resolution (`resolvedRef`), the status no longer changes on its own: it's
 * a one-time check, not a permanent toggle on every subsequent request —
 * denial/authorization/unauthenticated are ordinary, symmetric status
 * transitions (see the 2026-08-23 bug-fix history in git blame for why:
 * previously a single transient hiccup on any one channel could
 * permanently freeze the app on "Access Denied").
 *
 * 401 vs 403 distinction (added for the custom login page, 2026-08-26): 401
 * means "not logged in" — the SPA shows the Login page. 403 means "logged
 * in, but the AD account wasn't in the AdminConsole-Admins group at the
 * moment they logged in" — the SPA shows AccessDenied. Both used to
 * collapse into a single 'denied' status when the only way in was
 * Negotiate (which never produced a bare 401 to a browser that already had
 * Windows credentials).
 */
export function AuthProvider({ children }: { children: ReactNode }) {
  const [status, setStatus] = useState<AuthStatus>('checking')
  const resolvedRef = useRef(false)

  const reportDenied = useCallback(() => {
    resolvedRef.current = true
    setStatus('denied')
  }, [])

  const reportUnauthenticated = useCallback(() => {
    resolvedRef.current = true
    setStatus('unauthenticated')
  }, [])

  const reportAuthorized = useCallback(() => {
    resolvedRef.current = true
    setStatus('authorized')
  }, [])

  const recheck = useCallback(() => {
    getServers()
      .then(() => reportAuthorized())
      .catch((err: unknown) => {
        if (isAuthError(err)) {
          if (err.status === 401) reportUnauthenticated()
          else reportDenied()
        } else {
          // A network error/500 on the canary request shouldn't block the
          // app forever — only block on a confirmed 401/403.
          reportAuthorized()
        }
      })
  }, [reportAuthorized, reportDenied, reportUnauthenticated])

  useEffect(() => {
    recheck()
    // AuthProvider is mounted once at the app root and never unmounts for
    // the life of the tab — no cancellation guard needed here; a
    // development-mode StrictMode double-invoke just fires one extra
    // idempotent GET.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  return (
    <AuthContext.Provider value={{ status, reportDenied, reportUnauthenticated, reportAuthorized, recheck }}>
      {children}
    </AuthContext.Provider>
  )
}

export function useAuth(): AuthContextValue {
  const ctx = useContext(AuthContext)
  if (!ctx) throw new Error('useAuth must be used within AuthProvider')
  return ctx
}
