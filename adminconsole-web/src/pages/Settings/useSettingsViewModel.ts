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
 * T6.2 (Settings): unlike the dashboard hooks, there's no SignalR stream here —
 * plain REST requests with a manual refetch after every mutation (Save/Clear/
 * Add/Remove). That's enough for a page with rare, explicit admin actions.
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
        ? `Connection verified (Zabbix ${result.version}).`
        : `Token saved, but the connection check failed: ${result.error}`,
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

  const setZabbixMinSeverity = async (zabbixMinSeverity: number): Promise<boolean> => {
    if (!toggles) return false
    setTogglesSaving(true)
    try {
      setToggles(await updateMonitoringToggles({ ...toggles, zabbixMinSeverity }))
      return true
    } catch (err) {
      setError(err instanceof ApiError ? err : new ApiError(0, 'Unknown error'))
      return false
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
    setZabbixMinSeverity,
  }
}
