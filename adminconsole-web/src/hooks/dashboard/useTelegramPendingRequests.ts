import { useCallback, useEffect, useState } from 'react'
import { getTelegramPending } from '@/lib/api/endpoints'
import { useHubGroups } from '@/lib/signalr/useHubGroups'
import { useHubEvent } from '@/lib/signalr/useHubEvent'
import {
  TelegramAccessAction,
  type TelegramAccessChangedEvent,
  type TelegramAccessRequestEvent,
  type TelegramPendingRequest,
} from '@/lib/api/types'

// TelegramAccessRequestOccurred/TelegramAccessChangedOccurred летять у "logs" (SignalRBroadcastHandler).
const GROUPS = ['logs'] as const

/**
 * Аудит-фікс (2026-08-22, п.2): раніше веб-Settings не мали жодної
 * видимості в pending-запити доступу до бота — TelegramAccessRequestOccurred
 * стріляв, але фронтенд його ігнорував. REST-seed (GET /api/telegramusers/pending)
 * + живі оновлення: нова заявка (/start від неавторизованого) додається в
 * реальному часі, а Approve/Deny (з БУДЬ-ЯКОГО каналу — вебу чи inline-кнопки
 * в самому Telegram) прибирає її з обох UI одночасно.
 *
 * isPrimaryAdminClaimed навмисно НЕ оновлюється live — прив'язка Primary
 * Admin (через /claim_admin у самому Telegram) не має власної SignalR-події,
 * це одноразова bootstrap-дія; refetch() підхопить зміну.
 */
export function useTelegramPendingRequests() {
  const [pending, setPending] = useState<TelegramPendingRequest[]>([])
  const [isPrimaryAdminClaimed, setIsPrimaryAdminClaimed] = useState(false)
  const [loading, setLoading] = useState(true)

  const reconnectGeneration = useHubGroups(GROUPS)

  const refetch = useCallback(async () => {
    try {
      const data = await getTelegramPending()
      setPending(data.pending)
      setIsPrimaryAdminClaimed(data.isPrimaryAdminClaimed)
    } finally {
      setLoading(false)
    }
  }, [])

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

  return { pending, isPrimaryAdminClaimed, loading, refetch }
}
