import { useEffect, useRef, useState } from 'react'
import { Modal } from '@/components/ui/Modal'
import { StatusDot, type StatusTone } from '@/components/ui/StatusDot'
import { getPing } from '@/lib/api/endpoints'
import { PingStatus } from '@/lib/api/types'
import type { HostRow } from '@/hooks/dashboard/pingMath'
import styles from './ContinuousPingModal.module.scss'

interface PingEntry {
  at: string
  status: PingStatus
  latencyMs: number | null
}

const STATUS_TONE: Record<PingStatus, StatusTone> = {
  [PingStatus.Online]: 'success',
  [PingStatus.Offline]: 'critical',
  [PingStatus.Checking]: 'info',
  [PingStatus.Unknown]: 'inactive',
}

const MAX_ENTRIES = 100
const INTERVAL_MS = 1000

export interface ContinuousPingModalProps {
  host: HostRow
  onClose: () => void
}

/**
 * A web-native replacement for the WPF "Continuous Ping" (which opened a
 * separate cmd.exe window running `ping -t` on the admin's machine —
 * impossible to reproduce from a headless service, see
 * RemoteManagementService). Polls the existing GET /api/ping endpoint once a
 * second while the modal is open and filters the response down to a single
 * host — no new backend endpoint was needed.
 *
 * A real web limitation (agreed with the user): unlike the standalone
 * cmd.exe window in WPF, this "live" ping stops as soon as the modal is
 * closed or the component is unmounted.
 */
export function ContinuousPingModal({ host, onClose }: ContinuousPingModalProps) {
  const [entries, setEntries] = useState<PingEntry[]>([])
  const [error, setError] = useState<string | null>(null)
  const listRef = useRef<HTMLDivElement>(null)

  useEffect(() => {
    let cancelled = false

    const tick = async () => {
      try {
        const data = await getPing()
        if (cancelled) return
        const result = data.results.find((r) => r.ip === host.ip)
        setEntries((prev) =>
          [
            ...prev,
            {
              at: new Date().toISOString(),
              status: result?.status ?? PingStatus.Unknown,
              latencyMs: result?.latencyMs ?? null,
            },
          ].slice(-MAX_ENTRIES),
        )
        setError(null)
      } catch (err) {
        if (!cancelled) setError(err instanceof Error ? err.message : 'Unknown error.')
      }
    }

    tick()
    const id = setInterval(tick, INTERVAL_MS)
    return () => {
      cancelled = true
      clearInterval(id)
    }
  }, [host.ip])

  useEffect(() => {
    listRef.current?.scrollTo({ top: listRef.current.scrollHeight })
  }, [entries])

  const latest = entries.at(-1)

  return (
    <Modal title={`Continuous ping — ${host.name}`} onClose={onClose}>
      <div className={styles.summary}>
        <span className={styles.ip}>{host.ip}</span>
        {latest && (
          <span className={styles.latest}>
            <StatusDot tone={STATUS_TONE[latest.status]} />
            {latest.latencyMs != null ? `${latest.latencyMs}ms` : latest.status}
          </span>
        )}
      </div>

      {error && <p className={styles.error}>Ping request failed: {error}</p>}

      <div className={styles.list} ref={listRef}>
        {entries.length === 0 ? (
          <span className={styles.waiting}>Pinging…</span>
        ) : (
          entries.map((entry, index) => (
            <div className={styles.row} key={`${entry.at}-${index}`}>
              <StatusDot tone={STATUS_TONE[entry.status]} />
              <span className={styles.time}>{new Date(entry.at).toLocaleTimeString()}</span>
              <span className={styles.value}>
                {entry.status === PingStatus.Online
                  ? `Reply: ${entry.latencyMs ?? '—'}ms`
                  : entry.status === PingStatus.Offline
                    ? 'Request timed out'
                    : 'Unknown'}
              </span>
            </div>
          ))
        )}
      </div>
    </Modal>
  )
}
