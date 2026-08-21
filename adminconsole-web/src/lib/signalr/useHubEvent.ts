import { useEffect } from 'react'
import { useDashboardConnection } from './DashboardConnectionContext'

/** Підписка на один SignalR-метод (назва = typeof(T).Name на бекенді, див. SignalRBroadcastHandler). */
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
