import { useEffect } from 'react'
import { useDashboardConnection } from './DashboardConnectionContext'

/**
 * Приєднує компонент до SignalR-груп на час його життя (ref-counted, див.
 * DashboardConnectionContext). Повертає reconnectGeneration — додайте його
 * в залежності свого REST-фетч-ефекту (поруч із reportDenied), щоб дані
 * автоматично перезапитувались після reconnect, а не лишались тихо
 * застарілими (Аудит Зона 5, Знахідка №1, 2026-08-22).
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
