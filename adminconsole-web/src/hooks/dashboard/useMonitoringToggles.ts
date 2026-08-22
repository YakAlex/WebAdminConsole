import { useEffect, useState } from 'react'
import { getMonitoringToggles } from '@/lib/api/endpoints'
import { useHubGroups } from '@/lib/signalr/useHubGroups'
import { useHubEvent } from '@/lib/signalr/useHubEvent'
import { MonitoredService, type MonitoringToggledEvent, type MonitoringToggles } from '@/lib/api/types'

const GROUPS = ['logs'] as const

/**
 * Аудит-фікс п.4: жодна сторінка не звірялась зі станом тумблерів
 * моніторингу з Settings — вимкнення сервісу (напр. Zabbix) не приховувало
 * вже завантажені/застарілі дані деінде в застосунку (Overview продовжував
 * показувати останній відомий "12 problems"). REST-знімок (уже готовий
 * GET /api/monitoring/toggles) + живе оновлення через MonitoringToggledOccurred
 * (MonitoringController уже публікує цю подію на кожен Save — просто досі
 * ніхто на фронтенді її не слухав).
 *
 * `null` — тумблери ще не завантажені; свідомо НЕ гейтимо нічого до першої
 * відповіді, щоб не блимати "вимкнено" на кожному F5.
 */
export function useMonitoringToggles(): MonitoringToggles | null {
  const [toggles, setToggles] = useState<MonitoringToggles | null>(null)

  const reconnectGeneration = useHubGroups(GROUPS)

  useEffect(() => {
    let cancelled = false
    getMonitoringToggles()
      .then((data) => {
        if (!cancelled) setToggles(data)
      })
      .catch(() => {
        // Тумблери — не критичні дані сторінки: якщо цей запит впав, просто
        // нічого не гейтимо (лишається null) — основна помилка сторінки й
        // так покажеться через fetchError відповідного *Data-хука.
      })
    return () => {
      cancelled = true
    }
  }, [reconnectGeneration])

  useHubEvent<MonitoringToggledEvent>('MonitoringToggledOccurred', (evt) => {
    setToggles((prev) => {
      const base: MonitoringToggles = prev ?? {
        rdpMonitoringEnabled: true,
        zabbixMonitoringEnabled: true,
        backupMonitoringEnabled: true,
      }
      switch (evt.service) {
        case MonitoredService.Rdp:
          return { ...base, rdpMonitoringEnabled: evt.enabled }
        case MonitoredService.Zabbix:
          return { ...base, zabbixMonitoringEnabled: evt.enabled }
        case MonitoredService.Backups:
          return { ...base, backupMonitoringEnabled: evt.enabled }
        default:
          return base
      }
    })
  })

  return toggles
}
