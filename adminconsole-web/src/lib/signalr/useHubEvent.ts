import { useEffect } from 'react'
import { useDashboardConnection } from './DashboardConnectionContext'

/** Subscribes to a single SignalR method (name = typeof(T).Name on the backend, see SignalRBroadcastHandler). */
export function useHubEvent<T>(eventName: string, handler: (payload: T) => void): void {
  const { connection } = useDashboardConnection()

  useEffect(() => {
    const listener = (payload: T) => handler(payload)
    connection.on(eventName, listener)
    return () => {
      connection.off(eventName, listener)
    }
  }, [connection, eventName, handler])
}
