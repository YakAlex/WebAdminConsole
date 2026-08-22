import { useEffect, useState } from 'react'
import { getBackups } from '@/lib/api/endpoints'
import { ApiError, isAuthError } from '@/lib/api/http'
import { useAuth } from '@/lib/auth/AuthContext'
import { useHubGroups } from '@/lib/signalr/useHubGroups'
import { useHubEvent } from '@/lib/signalr/useHubEvent'
import type { BackupCheckState, BackupStatusUpdatedEvent } from '@/lib/api/types'

const GROUPS = ['backups'] as const

/** GET /api/backups (initial snapshot) + BackupStatusUpdatedOccurred (full snapshot once per cycle). */
export function useBackupsData() {
  const [states, setStates] = useState<BackupCheckState[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<ApiError | null>(null)
  const { reportDenied } = useAuth()

  const reconnectGeneration = useHubGroups(GROUPS)

  useEffect(() => {
    let cancelled = false

    getBackups()
      .then((data) => {
        if (!cancelled) setStates(data)
      })
      .catch((err: unknown) => {
        if (cancelled) return
        const apiError = err instanceof ApiError ? err : new ApiError(0, 'Unknown error')
        setError(apiError)
        if (isAuthError(apiError)) reportDenied()
      })
      .finally(() => {
        if (!cancelled) setLoading(false)
      })

    return () => {
      cancelled = true
    }
  }, [reportDenied, reconnectGeneration])

  useHubEvent<BackupStatusUpdatedEvent>('BackupStatusUpdatedOccurred', (evt) => setStates(evt.snapshot))

  return { states, loading, error }
}
