import { useCallback, useEffect, useState } from 'react'
import { getTelegramPending } from '@/lib/api/endpoints'
import { ApiError, isAuthError } from '@/lib/api/http'
import { useAuth } from '@/lib/auth/AuthContext'
import { useHubGroups } from '@/lib/signalr/useHubGroups'
import { useHubEvent } from '@/lib/signalr/useHubEvent'
import {
  TelegramAccessAction,
  type TelegramAccessChangedEvent,
  type TelegramAccessRequestEvent,
  type TelegramPendingRequest,
} from '@/lib/api/types'

// TelegramAccessRequestOccurred/TelegramAccessChangedOccurred are broadcast to "logs" (SignalRBroadcastHandler).
const GROUPS = ['logs'] as const

/**
 * Audit fix (2026-08-22, item 2): previously web Settings had no
 * visibility into pending bot access requests at all —
 * TelegramAccessRequestOccurred fired, but the frontend ignored it.
 * REST seed (GET /api/telegramusers/pending) + live updates: a new
 * request (/start from an unauthorized user) is added in real time,
 * and Approve/Deny (from ANY channel — the web or an inline button in
 * Telegram itself) removes it from both UIs at once.
 *
 * isPrimaryAdminClaimed is intentionally NOT updated live — claiming
 * Primary Admin (via /claim_admin in Telegram itself) has no SignalR
 * event of its own; it's a one-time bootstrap action, and refetch()
 * will pick up the change.
 */
export function useTelegramPendingRequests() {
  const [pending, setPending] = useState<TelegramPendingRequest[]>([])
  const [isPrimaryAdminClaimed, setIsPrimaryAdminClaimed] = useState(false)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<ApiError | null>(null)
  const { reportDenied } = useAuth()

  const reconnectGeneration = useHubGroups(GROUPS)

  // Audit Zone 6, Finding #1 (2026-08-22): previously there was no
  // catch here — any error (network, 401/403, 500) became an unhandled
  // promise rejection, and unlike every other hook in this class, a
  // 401/403 here never reached reportDenied() — the app-wide auth
  // state could miss an expired session specifically through this
  // endpoint.
  const refetch = useCallback(async () => {
    try {
      const data = await getTelegramPending()
      setPending(data.pending)
      setIsPrimaryAdminClaimed(data.isPrimaryAdminClaimed)
      setError(null)
    } catch (err: unknown) {
      const apiError = err instanceof ApiError ? err : new ApiError(0, 'Unknown error')
      setError(apiError)
      if (isAuthError(apiError)) reportDenied()
    } finally {
      setLoading(false)
    }
  }, [reportDenied])

  useEffect(() => {
    refetch()
  }, [refetch, reconnectGeneration])

  useHubEvent<TelegramAccessRequestEvent>('TelegramAccessRequestOccurred', (evt) => {
    setPending((prev) => (prev.some((p) => p.id === evt.request.id) ? prev : [...prev, evt.request]))
  })

  useHubEvent<TelegramAccessChangedEvent>('TelegramAccessChangedOccurred', (evt) => {
    if (evt.action === TelegramAccessAction.Approved || evt.action === TelegramAccessAction.Denied)
      setPending((prev) => prev.filter((p) => p.chatId !== evt.chatId))
  })

  return { pending, isPrimaryAdminClaimed, loading, error, refetch }
}
