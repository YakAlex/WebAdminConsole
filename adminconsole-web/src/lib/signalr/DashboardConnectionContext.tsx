import { createContext, useCallback, useContext, useEffect, useRef, useState, type ReactNode } from 'react'
import { HttpError, HubConnectionState, type HubConnection } from '@microsoft/signalr'
import { useAuth } from '@/lib/auth/AuthContext'
import { createDashboardConnection } from './connection'

interface DashboardConnectionValue {
  connection: HubConnection
  state: HubConnectionState
  joinGroup: (group: string) => void
  leaveGroup: (group: string) => void
}

const DashboardConnectionContext = createContext<DashboardConnectionValue | null>(null)

/**
 * Один спільний HubConnection на весь застосунок (монтується раз у main.tsx,
 * навколо <App/>). Групи (§DashboardHub: "ping"/"uptime"/"backups"/"logs")
 * підписуються через ref-counted joinGroup/leaveGroup — кілька хуків можуть
 * хотіти ту саму групу, а сервер бачить лише один Join/Leave виклик.
 *
 * Rejoin після reconnect теж централізований тут (ОДИН connection.onreconnected
 * на весь застосунок) — @microsoft/signalr не дає API для відписки від
 * onreconnected, тож реєструвати його з кожного окремого хука було б
 * витоком підписок при кожному ре-рендері.
 */
export function DashboardConnectionProvider({ children }: { children: ReactNode }) {
  const connectionRef = useRef<HubConnection | null>(null)
  if (!connectionRef.current) connectionRef.current = createDashboardConnection()
  const connection = connectionRef.current

  const [state, setState] = useState<HubConnectionState>(connection.state)
  const { reportDenied, reportAuthorized } = useAuth()
  const groupRefCounts = useRef(new Map<string, number>())

  const rejoinActiveGroups = useCallback(() => {
    for (const [group, count] of groupRefCounts.current) {
      if (count > 0) connection.invoke('JoinGroup', group).catch(() => {})
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
    <DashboardConnectionContext.Provider value={{ connection, state, joinGroup, leaveGroup }}>
      {children}
    </DashboardConnectionContext.Provider>
  )
}

export function useDashboardConnection(): DashboardConnectionValue {
  const ctx = useContext(DashboardConnectionContext)
  if (!ctx) throw new Error('useDashboardConnection must be used within DashboardConnectionProvider')
  return ctx
}
