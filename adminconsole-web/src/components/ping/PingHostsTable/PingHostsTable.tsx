import { useState } from 'react'
import { Server, RotateCw, Power, MonitorUp, Radar } from 'lucide-react'
import clsx from 'clsx'
import { StatusDot, type StatusTone } from '@/components/ui/StatusDot'
import { Modal } from '@/components/ui/Modal'
import { PingStatus, ServerType } from '@/lib/api/types'
import { formatClockWithSeconds } from '@/lib/format'
import { restartServer, shutdownServer, rdpFileUrl } from '@/lib/api/endpoints'
import type { HostRow } from '@/hooks/dashboard/pingMath'
import { ContinuousPingModal } from '@/components/ping/ContinuousPingModal/ContinuousPingModal'
import styles from './PingHostsTable.module.scss'

const STATUS_LABEL: Record<PingStatus, string> = {
  [PingStatus.Online]: 'Online',
  [PingStatus.Offline]: 'Offline',
  [PingStatus.Checking]: 'Checking',
  [PingStatus.Unknown]: 'Unknown',
}

const STATUS_TONE: Record<PingStatus, StatusTone> = {
  [PingStatus.Online]: 'success',
  [PingStatus.Offline]: 'critical',
  [PingStatus.Checking]: 'info',
  [PingStatus.Unknown]: 'inactive',
}

const STATUS_TEXT_CLASS: Record<StatusTone, string> = {
  success: 'statusSuccess',
  warning: 'statusCritical',
  critical: 'statusCritical',
  inactive: 'statusInactive',
  info: 'statusInactive',
}

type ActionKind = 'restart' | 'shutdown'

interface PendingAction {
  host: HostRow
  kind: ActionKind
}

type ActionPhase = 'confirm' | 'busy' | { result: 'success' | 'error'; message: string }

const ACTION_COPY: Record<ActionKind, { verb: string; danger: boolean; warning: string }> = {
  restart: {
    verb: 'Restart',
    danger: false,
    warning: 'The server will reboot and come back on its own once Windows finishes starting.',
  },
  shutdown: {
    verb: 'Shutdown',
    danger: true,
    warning: 'The server will power off and will NOT come back on its own — someone needs physical or remote-power access to turn it back on.',
  },
}

export interface PingHostsTableProps {
  hosts: HostRow[]
}

/**
 * §26 брифу (Ping): "Hosts table" — усі сервери зі статусом, IP, response time.
 * Пріоритет 3, #3.1: колонка Actions — Restart/Shutdown/RDP (лише Windows,
 * той самий принцип, що й у WPF PingResultViewModel.IsWindows) + Continuous
 * Ping (усі типи пристроїв).
 */
export function PingHostsTable({ hosts }: PingHostsTableProps) {
  const [pending, setPending] = useState<PendingAction | null>(null)
  const [phase, setPhase] = useState<ActionPhase>('confirm')
  const [continuousPingHost, setContinuousPingHost] = useState<HostRow | null>(null)

  const openConfirm = (host: HostRow, kind: ActionKind) => {
    setPending({ host, kind })
    setPhase('confirm')
  }

  const closeAction = () => {
    setPending(null)
    setPhase('confirm')
  }

  const runAction = async () => {
    if (!pending) return
    setPhase('busy')
    try {
      const result =
        pending.kind === 'restart' ? await restartServer(pending.host.ip) : await shutdownServer(pending.host.ip)
      setPhase(
        result.success
          ? { result: 'success', message: `${ACTION_COPY[pending.kind].verb} command accepted by ${pending.host.name}.` }
          : { result: 'error', message: result.error ?? 'Unknown error.' },
      )
    } catch (err) {
      setPhase({ result: 'error', message: err instanceof Error ? err.message : 'Unknown error.' })
    }
  }

  return (
    <div className={styles.card}>
      <div className={styles.header}>
        <span className={styles.eyebrow}>Hosts</span>
      </div>

      {hosts.length === 0 ? (
        <div className={styles.empty}>No servers configured</div>
      ) : (
        <div className={styles.tableWrap}>
          <table className={styles.table}>
            <thead>
              <tr>
                <th>Host</th>
                <th>Status</th>
                <th>IP</th>
                <th>Group</th>
                <th>Response time</th>
                <th>Last checked</th>
                <th aria-label="Actions" />
              </tr>
            </thead>
            <tbody>
              {hosts.map((host) => (
                <tr key={host.ip}>
                  <td>
                    <span className={styles.host}>
                      <Server size={14} strokeWidth={1.75} className={styles.hostIcon} />
                      {host.name}
                    </span>
                  </td>
                  <td>
                    <span className={clsx(styles.status, styles[STATUS_TEXT_CLASS[STATUS_TONE[host.status]]])}>
                      <StatusDot tone={STATUS_TONE[host.status]} />
                      {STATUS_LABEL[host.status]}
                    </span>
                  </td>
                  <td className={styles.ip}>{host.ip}</td>
                  <td className={styles.group}>{host.group}</td>
                  <td className={styles.latency}>{host.latencyMs != null ? `${host.latencyMs}ms` : '—'}</td>
                  <td className={styles.lastChecked}>{formatClockWithSeconds(host.lastChecked)}</td>
                  <td className={styles.actions}>
                    <button
                      type="button"
                      className={styles.actionButton}
                      onClick={() => setContinuousPingHost(host)}
                      title="Continuous ping"
                      aria-label={`Continuous ping ${host.name}`}
                    >
                      <Radar size={14} strokeWidth={1.75} />
                    </button>
                    {host.type === ServerType.Windows && (
                      <>
                        <a
                          className={styles.actionButton}
                          href={rdpFileUrl(host.ip)}
                          title="Start RDP session"
                          aria-label={`Start RDP session to ${host.name}`}
                        >
                          <MonitorUp size={14} strokeWidth={1.75} />
                        </a>
                        <button
                          type="button"
                          className={styles.actionButton}
                          onClick={() => openConfirm(host, 'restart')}
                          title="Restart"
                          aria-label={`Restart ${host.name}`}
                        >
                          <RotateCw size={14} strokeWidth={1.75} />
                        </button>
                        <button
                          type="button"
                          className={clsx(styles.actionButton, styles.danger)}
                          onClick={() => openConfirm(host, 'shutdown')}
                          title="Shutdown"
                          aria-label={`Shutdown ${host.name}`}
                        >
                          <Power size={14} strokeWidth={1.75} />
                        </button>
                      </>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {pending && (
        <Modal
          title={typeof phase === 'object' ? (phase.result === 'success' ? 'Success' : 'Failed') : `${ACTION_COPY[pending.kind].verb} ${pending.host.name}?`}
          onClose={closeAction}
          footer={
            typeof phase === 'object' ? (
              <button type="button" className={styles.cancelButton} onClick={closeAction}>
                Close
              </button>
            ) : (
              <>
                <button type="button" className={styles.cancelButton} onClick={closeAction} disabled={phase === 'busy'}>
                  Cancel
                </button>
                <button
                  type="button"
                  className={clsx(styles.confirmButton, ACTION_COPY[pending.kind].danger && styles.danger)}
                  onClick={runAction}
                  disabled={phase === 'busy'}
                >
                  {phase === 'busy' ? 'Sending…' : `Yes, ${ACTION_COPY[pending.kind].verb.toLowerCase()}`}
                </button>
              </>
            )
          }
        >
          {typeof phase === 'object' ? (
            <p className={clsx(styles.modalMessage, phase.result === 'error' && styles.errorMessage)}>{phase.message}</p>
          ) : (
            <>
              <p className={styles.modalMessage}>
                Are you sure you want to {ACTION_COPY[pending.kind].verb.toLowerCase()}{' '}
                <strong>{pending.host.name}</strong> ({pending.host.ip})?
              </p>
              <p className={styles.modalWarning}>{ACTION_COPY[pending.kind].warning}</p>
            </>
          )}
        </Modal>
      )}

      {continuousPingHost && (
        <ContinuousPingModal host={continuousPingHost} onClose={() => setContinuousPingHost(null)} />
      )}
    </div>
  )
}
