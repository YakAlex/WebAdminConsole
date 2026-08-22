import { useEffect } from 'react'
import { useDashboardConnection } from './DashboardConnectionContext'

/**
 * Joins the component to SignalR groups for its lifetime (ref-counted,
 * see DashboardConnectionContext). Returns reconnectGeneration — add it
 * to your REST fetch effect's dependencies (alongside reportDenied) so
 * data automatically refetches after a reconnect instead of staying
 * silently stale (Audit Zone 5, Finding #1, 2026-08-22).
 */
export function useHubGroups(groups: readonly string[]): number {
  const { joinGroup, leaveGroup, reconnectGeneration } = useDashboardConnection()
  const key = groups.join(',')

  useEffect(() => {
    const list = key.split(',').filter(Boolean)
    list.forEach(joinGroup)
    return () => list.forEach(leaveGroup)
  }, [key, joinGroup, leaveGroup])

  return reconnectGeneration
}
