import { Server } from 'lucide-react'
import clsx from 'clsx'
import { StatusDot, type StatusTone } from '@/components/ui/StatusDot'
import { PingStatus } from '@/lib/api/types'
import { formatClockWithSeconds } from '@/lib/format'
import type { HostRow } from '@/hooks/dashboard/pingMath'
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

export interface PingHostsTableProps {
  hosts: HostRow[]
}

/** §26 брифу (Ping): "Hosts table" — усі сервери зі статусом, IP, response time. */
export function PingHostsTable({ hosts }: PingHostsTableProps) {
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
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  )
}
