import { useEffect, useState } from 'react'
import { getServers } from '@/lib/api/endpoints'
import { ApiError, isAuthError } from '@/lib/api/http'
import { useAuth } from '@/lib/auth/AuthContext'
import type { ServerEntry } from '@/lib/api/types'

/** GET /api/servers — a static list (appsettings.json), no SignalR updates. */
export function useServers() {
  const [servers, setServers] = useState<ServerEntry[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<ApiError | null>(null)
  const { reportDenied } = useAuth()

  useEffect(() => {
    let cancelled = false

    getServers()
      .then((data) => {
        if (!cancelled) setServers(data)
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
  }, [reportDenied])

  return { servers, loading, error }
}
