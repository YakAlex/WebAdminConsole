import { createContext, useCallback, useContext, useEffect, useRef, useState, type ReactNode } from 'react'
import { HttpError, HubConnectionState, type HubConnection } from '@microsoft/signalr'
import { useAuth } from '@/lib/auth/AuthContext'
import { createDashboardConnection } from './connection'

interface DashboardConnectionValue {
  connection: HubConnection
  state: HubConnectionState
  /**
   * Audit Zone 5, Finding #1 (2026-08-22): group rejoin after reconnect
   * already existed (rejoinActiveGroups), but no data hook refetched
   * REST after a successful reconnect — SignalR groups were correctly
   * rejoined, but data missed during the disconnect was never caught
   * up (silently stale state, no error shown in the UI). Incremented
   * on every onreconnected; hooks add this value to their REST
   * effect's dependencies (useHubGroups returns it) so REST is
   * automatically refetched after a reconnect.
   */
  reconnectGeneration: number
  joinGroup: (group: string) => void
  leaveGroup: (group: string) => void
}

const DashboardConnectionContext = createContext<DashboardConnectionValue | null>(null)

/**
 * A single shared HubConnection for the whole app (mounted once in
 * main.tsx, around <App/>). Groups (§DashboardHub:
 * "ping"/"uptime"/"backups"/"logs") are subscribed to via ref-counted
 * joinGroup/leaveGroup — several hooks may want the same group, while
 * the server only sees a single Join/Leave call.
 *
 * Rejoin after reconnect is also centralized here (ONE
 * connection.onreconnected for the whole app) — @microsoft/signalr
 * provides no API to unsubscribe from onreconnected, so registering it
 * from each individual hook would leak subscriptions on every
 * re-render.
 */
export function DashboardConnectionProvider({ children }: { children: ReactNode }) {
  const connectionRef = useRef<HubConnection | null>(null)
  if (!connectionRef.current) connectionRef.current = createDashboardConnection()
  const connection = connectionRef.current

  const [state, setState] = useState<HubConnectionState>(connection.state)
  const [reconnectGeneration, setReconnectGeneration] = useState(0)
  const { reportDenied, reportAuthorized } = useAuth()
  const groupRefCounts = useRef(new Map<string, number>())

  const rejoinActiveGroups = useCallback(() => {
    for (const [group, count] of groupRefCounts.current) {
      if (count > 0) {
        // Audit Zone 5, Finding #2 (2026-08-22): previously .catch(() => {})
        // silently swallowed any rejoin error — if THIS group specifically
        // failed to rejoin after a reconnect, the corresponding part of the
        // UI would silently be left without live updates, with no log or
        // indicator at all.
        connection
          .invoke('JoinGroup', group)
          .catch((err: unknown) => console.warn(`[SignalR] rejoin group "${group}" failed after reconnect:`, err))
      }
    }
  }, [connection])

  const joinGroup = useCallback(
    (group: string) => {
      const current = groupRefCounts.current.get(group) ?? 0
      groupRefCounts.current.set(group, current + 1)
      if (current === 0 && connection.state === HubConnectionState.Connected) {
        connection.invoke('JoinGroup', group).catch(() => {})
      }
    },
    [connection],
  )

  const leaveGroup = useCallback(
    (group: string) => {
      const current = groupRefCounts.current.get(group) ?? 0
      const next = Math.max(current - 1, 0)

      if (next === 0) {
        groupRefCounts.current.delete(group)
        if (connection.state === HubConnectionState.Connected) {
          connection.invoke('LeaveGroup', group).catch(() => {})
        }
      } else {
        groupRefCounts.current.set(group, next)
      }
    },
    [connection],
  )

  useEffect(() => {
    const updateState = () => setState(connection.state)

    connection.onreconnecting(updateState)
    connection.onreconnected(() => {
      updateState()
      rejoinActiveGroups()
      // Signal for data hooks (via useHubGroups) to refetch REST —
      // rejoining groups alone doesn't catch up on events missed during
      // the disconnect.
      setReconnectGeneration((g) => g + 1)
    })
    connection.onclose(updateState)

    connection
      .start()
      .then(() => {
        updateState()
        rejoinActiveGroups()
        reportAuthorized()
      })
      .catch((err: unknown) => {
        updateState()
        if (err instanceof HttpError && (err.statusCode === 401 || err.statusCode === 403)) {
          reportDenied()
        }
      })

    return () => {
      connection.stop().catch(() => {})
    }
  }, [connection, rejoinActiveGroups, reportAuthorized, reportDenied])

  return (
    <DashboardConnectionContext.Provider value={{ connection, state, reconnectGeneration, joinGroup, leaveGroup }}>
      {children}
    </DashboardConnectionContext.Provider>
  )
}

export function useDashboardConnection(): DashboardConnectionValue {
  const ctx = useContext(DashboardConnectionContext)
  if (!ctx) throw new Error('useDashboardConnection must be used within DashboardConnectionProvider')
  return ctx
}
