import { useMemo, useState } from 'react'
import { Router } from 'lucide-react'
import { StatusDot, type StatusTone } from '@/components/ui/StatusDot'
import { Sparkline } from '@/components/ui/Sparkline'
import { FilterSelect } from '@/components/ui/FilterSelect'
import { PingStatus } from '@/lib/api/types'
import { formatClockWithSeconds } from '@/lib/format'
import styles from './UptimeByDeviceTable.module.scss'

const ALL_GROUPS = 'all'

export interface DeviceRow {
  ip: string
  name: string
  group: string
  status: PingStatus
  uptimePercent: number
  trend: number[]
  responseTimeMs: number | null
  lastCheck: string | null
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

export interface UptimeByDeviceTableProps {
  rows: DeviceRow[]
}

/** §17 брифу: широка операційна таблиця, рядки ~34–38px. Дані — servers + usePingStream() + downtime-тренд. */
export function UptimeByDeviceTable({ rows }: UptimeByDeviceTableProps) {
  const [group, setGroup] = useState(ALL_GROUPS)

  const groupOptions = useMemo(() => {
    const distinct = Array.from(new Set(rows.map((r) => r.group).filter(Boolean))).sort()
    return [{ value: ALL_GROUPS, label: 'All Devices' }, ...distinct.map((g) => ({ value: g, label: g }))]
  }, [rows])

  const visibleRows = group === ALL_GROUPS ? rows : rows.filter((r) => r.group === group)

  return (
    <div className={styles.card}>
      <div className={styles.header}>
        <span className={styles.eyebrow}>Uptime by Device</span>
        <FilterSelect value={group} onChange={setGroup} options={groupOptions} ariaLabel="Filter devices by group" />
      </div>

      <div className={styles.tableWrap}>
        <table className={styles.table}>
          <thead>
            <tr>
              <th>Device</th>
              <th>Status</th>
              <th>Uptime</th>
              <th>Trend</th>
              <th>Response time</th>
              <th>Last check</th>
            </tr>
          </thead>
          <tbody>
            {visibleRows.map((device) => (
              <tr key={device.ip}>
                <td>
                  <span className={styles.device}>
                    <Router size={14} strokeWidth={1.75} className={styles.deviceIcon} />
                    {device.name}
                  </span>
                </td>
                <td>
                  <span className={styles.status}>
                    <StatusDot tone={STATUS_TONE[device.status]} />
                    {STATUS_LABEL[device.status]}
                  </span>
                </td>
                <td className={styles.uptime}>{device.uptimePercent.toFixed(2)}%</td>
                <td className={styles.trendCell}>
                  <Sparkline data={device.trend} width={64} height={20} strokeWidth={1.25} color="var(--color-success)" />
                </td>
                <td className={styles.response}>{device.responseTimeMs != null ? `${device.responseTimeMs}ms` : '—'}</td>
                <td className={styles.lastCheck}>{formatClockWithSeconds(device.lastCheck)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  )
}
