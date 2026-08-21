import { useEffect } from 'react'
import { useDashboardConnection } from './DashboardConnectionContext'

/** Приєднує компонент до SignalR-груп на час його життя (ref-counted, див. DashboardConnectionContext). */
export function useHubGroups(groups: readonly string[]): void {
  const { joinGroup, leaveGroup } = useDashboardConnection()
  const key = groups.join(',')

  useEffect(() => {
    const list = key.split(',').filter(Boolean)
    list.forEach(joinGroup)
    return () => list.forEach(leaveGroup)
  }, [key, joinGroup, leaveGroup])
}
