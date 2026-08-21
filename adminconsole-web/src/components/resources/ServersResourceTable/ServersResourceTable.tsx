import { useMemo, useState } from 'react'
import { Server } from 'lucide-react'
import clsx from 'clsx'
import { StatusDot, type StatusTone } from '@/components/ui/StatusDot'
import { FilterSelect } from '@/components/ui/FilterSelect'
import { PingStatus, ServerType } from '@/lib/api/types'
import styles from './ServersResourceTable.module.scss'

const ALL_SERVERS = 'all'

export interface ServerResourceRow {
  ip: string
  name: string
  group: string
  type: ServerType
  status: PingStatus
}

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

const TYPE_LABEL: Record<ServerType, string> = {
  [ServerType.Windows]: 'Windows',
  [ServerType.Linux]: 'Linux',
  [ServerType.Network]: 'Network',
}

export interface ServersResourceTableProps {
  rows: ServerResourceRow[]
}

/**
 * §26 брифу (Resources): "таблиця серверів зі споживанням". `rows` приходить
 * уже відфільтрованим на Windows-only (useResourcesPageViewModel, #9) —
 * селектор праворуч звужує видимі рядки до одного конкретного сервера.
 * CPU/Memory — "Not monitored": ResourceMonitorService на бекенді відстежує лише
 * локальний хост AdminConsole, per-server телеметрії наразі немає — чесно
 * показуємо це замість вигаданих чисел (той самий принцип, що прибрав
 * "Disk Usage" з Overview на Кроці 3).
 */
export function ServersResourceTable({ rows }: ServersResourceTableProps) {
  const [selectedServer, setSelectedServer] = useState(ALL_SERVERS)

  const serverOptions = useMemo(() => {
    const sorted = [...rows].sort((a, b) => a.name.localeCompare(b.name))
    return [{ value: ALL_SERVERS, label: 'All Servers' }, ...sorted.map((r) => ({ value: r.ip, label: r.name }))]
  }, [rows])

  const visibleRows = selectedServer === ALL_SERVERS ? rows : rows.filter((r) => r.ip === selectedServer)

  return (
    <div className={styles.card}>
      <div className={styles.header}>
        <span className={styles.eyebrow}>Servers</span>
        <FilterSelect
          value={selectedServer}
          onChange={setSelectedServer}
          options={serverOptions}
          ariaLabel="Select a server"
        />
      </div>

      <div className={styles.tableWrap}>
        <table className={styles.table}>
          <thead>
            <tr>
              <th>Server</th>
              <th>Group</th>
              <th>Type</th>
              <th>Ping status</th>
              <th>CPU</th>
              <th>Memory</th>
            </tr>
          </thead>
          <tbody>
            {visibleRows.map((row) => (
              <tr key={row.ip}>
                <td>
                  <span className={styles.server}>
                    <Server size={14} strokeWidth={1.75} className={styles.serverIcon} />
                    {row.name}
                  </span>
                </td>
                <td className={styles.group}>{row.group}</td>
                <td className={styles.type}>{TYPE_LABEL[row.type]}</td>
                <td>
                  <span className={clsx(styles.status, styles[STATUS_TEXT_CLASS[STATUS_TONE[row.status]]])}>
                    <StatusDot tone={STATUS_TONE[row.status]} />
                    {STATUS_LABEL[row.status]}
                  </span>
                </td>
                <td className={styles.metric}>Not monitored</td>
                <td className={styles.metric}>Not monitored</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  )
}
