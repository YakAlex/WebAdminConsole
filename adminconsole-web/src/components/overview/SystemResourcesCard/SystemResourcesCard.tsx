import { Cpu, MemoryStick, ChevronRight } from 'lucide-react'
import { useNavigate } from 'react-router-dom'
import { Sparkline } from '@/components/ui/Sparkline'
import type { ResourceSnapshot } from '@/lib/api/types'
import styles from './SystemResourcesCard.module.scss'

export interface SystemResourcesCardProps {
  history: ResourceSnapshot[]
}

/**
 * §16 брифу. Дані — з useResourceSnapshot() (SignalR ResourceSnapshotUpdatedOccurred).
 * Рядок "Disk Usage" з оригінального мок-макета прибрано: ResourceSnapshot
 * на бекенді не містить метрики диска (лише CpuPercent/RamPercent) —
 * показувати вигадане число суперечило б самій меті цього кроку.
 * Крок 1 UX-polish (2026-08-21): прибрано декоративний "All Systems"
 * dropdown — ResourceMonitorService стежить лише за локальним хостом
 * AdminConsole, per-server телеметрії немає (як і в ResourcesOverview на
 * сторінці Resources), тож фільтрувати тут нічого.
 */
export function SystemResourcesCard({ history }: SystemResourcesCardProps) {
  const navigate = useNavigate()
  const latest = history.at(-1)
  const cpuTrend = history.map((s) => s.cpuPercent)
  const ramTrend = history.map((s) => s.ramPercent)

  return (
    <div className={styles.card}>
      <div className={styles.header}>
        <span className={styles.eyebrow}>System Resources</span>
        <span className={styles.hostLabel}>Local Host</span>
      </div>

      {!latest ? (
        <div className={styles.empty}>Waiting for resource data…</div>
      ) : (
        <div className={styles.list}>
          <div className={styles.row}>
            <span className={styles.label}>
              <Cpu size={14} strokeWidth={1.75} />
              CPU Usage
            </span>
            <div className={styles.metricLine}>
              <Sparkline
                data={cpuTrend}
                width={200}
                height={24}
                strokeWidth={1.5}
                color="var(--color-brand-cyan)"
                className={styles.chart}
              />
              <span className={styles.value}>{Math.round(latest.cpuPercent)}%</span>
            </div>
          </div>

          <div className={styles.row}>
            <span className={styles.label}>
              <MemoryStick size={14} strokeWidth={1.75} />
              Memory Usage
            </span>
            <div className={styles.metricLine}>
              <Sparkline
                data={ramTrend}
                width={200}
                height={24}
                strokeWidth={1.5}
                color="var(--color-warning)"
                className={styles.chart}
              />
              <span className={styles.value}>{Math.round(latest.ramPercent)}%</span>
            </div>
          </div>
        </div>
      )}

      <div className={styles.footer}>
        <button type="button" className={styles.footerLink} onClick={() => navigate('/resources')}>
          View all resources
          <ChevronRight size={13} strokeWidth={2} />
        </button>
      </div>
    </div>
  )
}
