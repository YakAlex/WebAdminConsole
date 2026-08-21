import { useState } from 'react'
import { useHubGroups } from '@/lib/signalr/useHubGroups'
import { useHubEvent } from '@/lib/signalr/useHubEvent'
import type { ZabbixProblemsPayload, ZabbixProblemsUpdatedEvent } from '@/lib/api/types'

const GROUPS = ['logs'] as const

export function useZabbixProblems(): ZabbixProblemsPayload | null {
  const [payload, setPayload] = useState<ZabbixProblemsPayload | null>(null)

  useHubGroups(GROUPS)
  useHubEvent<ZabbixProblemsUpdatedEvent>('ZabbixProblemsUpdatedOccurred', (evt) => setPayload(evt.payload))

  return payload
}
