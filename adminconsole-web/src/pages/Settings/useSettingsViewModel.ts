import { useCallback, useEffect, useState } from 'react'
import {
  clearTelegramCredentials,
  clearZabbixCredentials,
  getCredentials,
  getMonitoringToggles,
  getTelegramUsers,
  removeTelegramUser,
  saveTelegramToken,
  saveZabbixToken,
  updateMonitoringToggles,
} from '@/lib/api/endpoints'
import { ApiError } from '@/lib/api/http'
import type { CredentialsStatusResponse, MonitoringToggles, TelegramAllowedUserView } from '@/lib/api/types'
import type { MonitoredServiceKey } from '@/components/settings/MonitoringTogglesCard/MonitoringTogglesCard'

/**
 * T6.2 (Settings): на відміну від dashboard-хуків тут немає SignalR-потоку —
 * прості REST-запити з ручним refetch після кожної мутації (Save/Clear/
 * Add/Remove). Достатньо для сторінки з рідкісними, явними діями адміна.
 */
export function useSettingsViewModel() {
  const [credentials, setCredentials] = useState<CredentialsStatusResponse | null>(null)
  const [users, setUsers] = useState<TelegramAllowedUserView[]>([])
  const [toggles, setToggles] = useState<MonitoringToggles | null>(null)
  const [togglesSaving, setTogglesSaving] = useState(false)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<ApiError | null>(null)

  const refetch = useCallback(async () => {
    try {
      const [credentialsData, usersData, togglesData] = await Promise.all([
        getCredentials(),
        getTelegramUsers(),
        getMonitoringToggles(),
      ])
      setCredentials(credentialsData)
      setUsers(usersData)
      setToggles(togglesData)
      setError(null)
    } catch (err) {
      setError(err instanceof ApiError ? err : new ApiError(0, 'Unknown error'))
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => {
    refetch()
  }, [refetch])

  const saveZabbix = async (token: string) => {
    const result = await saveZabbixToken(token)
    await refetch()
    return {
      ok: result.success,
      message: result.success
        ? `Підключення підтверджено (Zabbix ${result.version}).`
        : `Токен збережено, але перевірка з'єднання не пройшла: ${result.error}`,
    }
  }

  const clearZabbix = async () => {
    await clearZabbixCredentials()
    await refetch()
  }

  const saveTelegram = async (botToken: string) => {
    await saveTelegramToken(botToken)
    await refetch()
  }

  const clearTelegram = async () => {
    await clearTelegramCredentials()
    await refetch()
  }

  const removeUser = async (chatId: number) => {
    await removeTelegramUser(chatId)
    await refetch()
  }

  const toggleMonitoring = async (service: MonitoredServiceKey, enabled: boolean) => {
    if (!toggles) return
    const next: MonitoringToggles = {
      ...toggles,
      zabbixMonitoringEnabled: service === 'zabbix' ? enabled : toggles.zabbixMonitoringEnabled,
      rdpMonitoringEnabled: service === 'rdp' ? enabled : toggles.rdpMonitoringEnabled,
      backupMonitoringEnabled: service === 'backup' ? enabled : toggles.backupMonitoringEnabled,
    }
    setTogglesSaving(true)
    try {
      setToggles(await updateMonitoringToggles(next))
    } catch (err) {
      setError(err instanceof ApiError ? err : new ApiError(0, 'Unknown error'))
    } finally {
      setTogglesSaving(false)
    }
  }

  return {
    credentials,
    users,
    toggles,
    togglesSaving,
    loading,
    error,
    saveZabbix,
    clearZabbix,
    saveTelegram,
    clearTelegram,
    removeUser,
    toggleMonitoring,
  }
}
