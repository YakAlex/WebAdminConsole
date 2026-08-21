import { useState } from 'react'
import clsx from 'clsx'
import { Modal } from '@/components/ui/Modal'
import { endMaintenance, startMaintenance } from '@/lib/api/endpoints'
import { formatMaintenanceSchedule } from '@/lib/format'
import type { MaintenanceWindow } from '@/lib/api/types'
import type { HostRow } from '@/hooks/dashboard/pingMath'
import styles from './MaintenanceModal.module.scss'

export interface MaintenanceModalProps {
  host: HostRow
  /** Активне вікно для цього сервера/групи, якщо є — перемикає модалку в режим "End" замість "Start". */
  activeWindow: MaintenanceWindow | null
  onClose: () => void
}

type Scope = 'server' | 'group'
type DurationChoice = 10 | 30 | 60 | 'indefinite'

const DURATION_OPTIONS: { value: DurationChoice; label: string }[] = [
  { value: 10, label: '10 min' },
  { value: 30, label: '30 min' },
  { value: 60, label: '60 min' },
  { value: 'indefinite', label: 'Until ended' },
]

type Phase = 'form' | 'busy' | { result: 'error'; message: string }

/**
 * Аудит-фікс (2026-08-22, п.1): веб-нативний аналог WPF MaintenanceDialog —
 * запускає/завершує MaintenanceWindow через REST-шар над MaintenanceService,
 * який існував у бекенді з Фази 4 без жодного UI над ним. Ті самі
 * пресети тривалості (10/30/60/без обмеження), що й у WPF.
 */
export function MaintenanceModal({ host, activeWindow, onClose }: MaintenanceModalProps) {
  const [scope, setScope] = useState<Scope>('server')
  const [duration, setDuration] = useState<DurationChoice>(30)
  const [reason, setReason] = useState('')
  const [phase, setPhase] = useState<Phase>('form')

  const isEnding = activeWindow !== null
  const windowKey = activeWindow
    ? (activeWindow.targetGroup ? `group:${activeWindow.targetGroup}` : (activeWindow.serverIp ?? activeWindow.displayName))
    : null

  const submit = async () => {
    setPhase('busy')
    try {
      if (isEnding && windowKey) {
        await endMaintenance(windowKey)
      } else {
        await startMaintenance({
          serverIp: scope === 'server' ? host.ip : undefined,
          targetGroup: scope === 'group' ? host.group : undefined,
          durationMinutes: duration === 'indefinite' ? undefined : duration,
          reason: reason.trim() || undefined,
        })
      }
      onClose()
    } catch (err) {
      setPhase({ result: 'error', message: err instanceof Error ? err.message : 'Unknown error.' })
    }
  }

  if (isEnding && activeWindow) {
    return (
      <Modal
        title={`End maintenance — ${activeWindow.displayName}`}
        onClose={onClose}
        footer={
          <>
            <button type="button" className={styles.cancelButton} onClick={onClose} disabled={phase === 'busy'}>
              Cancel
            </button>
            <button type="button" className={styles.confirmButton} onClick={submit} disabled={phase === 'busy'}>
              {phase === 'busy' ? 'Ending…' : 'Yes, end maintenance'}
            </button>
          </>
        }
      >
        <p className={styles.message}>
          Under maintenance since {formatMaintenanceSchedule(activeWindow.from, activeWindow.to)}
          {activeWindow.reason && <> — “{activeWindow.reason}”</>}. Ping and backup alerts are suppressed while active.
        </p>
        {typeof phase === 'object' && <p className={styles.errorMessage}>{phase.message}</p>}
      </Modal>
    )
  }

  return (
    <Modal
      title={`Start maintenance — ${host.name}`}
      onClose={onClose}
      footer={
        <>
          <button type="button" className={styles.cancelButton} onClick={onClose} disabled={phase === 'busy'}>
            Cancel
          </button>
          <button type="button" className={styles.confirmButton} onClick={submit} disabled={phase === 'busy'}>
            {phase === 'busy' ? 'Starting…' : 'Start maintenance'}
          </button>
        </>
      }
    >
      <div className={styles.field}>
        <span className={styles.label}>Scope</span>
        <div className={styles.radioRow}>
          <label className={styles.radio}>
            <input type="radio" checked={scope === 'server'} onChange={() => setScope('server')} />
            This server ({host.name})
          </label>
          <label className={styles.radio}>
            <input type="radio" checked={scope === 'group'} onChange={() => setScope('group')} />
            Entire group ({host.group})
          </label>
        </div>
      </div>

      <div className={styles.field}>
        <span className={styles.label}>Duration</span>
        <div className={styles.durationRow}>
          {DURATION_OPTIONS.map((opt) => (
            <button
              key={opt.value}
              type="button"
              className={clsx(styles.durationOption, duration === opt.value && styles.durationOptionActive)}
              onClick={() => setDuration(opt.value)}
            >
              {opt.label}
            </button>
          ))}
        </div>
      </div>

      <label className={styles.field}>
        <span className={styles.label}>Reason (optional)</span>
        <input
          type="text"
          className={styles.input}
          placeholder="e.g. Windows updates"
          value={reason}
          onChange={(e) => setReason(e.target.value)}
        />
      </label>

      {typeof phase === 'object' && <p className={styles.errorMessage}>{phase.message}</p>}
    </Modal>
  )
}
