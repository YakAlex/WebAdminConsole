import { Cpu, MemoryStick } from 'lucide-react'
import { Card, CardHeader } from '@/components/ui/Card'
import { Sparkline } from '@/components/ui/Sparkline'
import type { ResourceSnapshot } from '@/lib/api/types'
import styles from './ResourcesOverview.module.scss'

export interface ResourcesOverviewProps {
  history: ResourceSnapshot[]
}

/**
 * §26 брифу (Resources): "CPU / RAM overview". ResourceSnapshot на бекенді
 * описує лише локальну машину AdminConsole (ResourceMonitorService — один
 * локальний опитувальний цикл, без per-server телеметрії) — тому це один
 * блок "Local Host", а не по одному на кожен сервер.
 */
export function ResourcesOverview({ history }: ResourcesOverviewProps) {
  const latest = history.at(-1)
  const cpuTrend = history.map((s) => s.cpuPercent)
  const ramTrend = history.map((s) => s.ramPercent)

  return (
    <Card>
      <CardHeader eyebrow="Local Host Resources" icon={<Cpu size={14} strokeWidth={1.75} />} />

      {!latest ? (
        <div className={styles.empty}>Waiting for resource data…</div>
      ) : (
        <div className={styles.metrics}>
          <div className={styles.metric}>
            <div className={styles.metricHeader}>
              <span className={styles.metricLabel}>
                <Cpu size={14} strokeWidth={1.75} />
                CPU Usage
              </span>
              <span className={styles.metricValue}>{Math.round(latest.cpuPercent)}%</span>
            </div>
            <Sparkline
              data={cpuTrend}
              variant="area"
              width={600}
              height={48}
              strokeWidth={1.5}
              color="var(--color-brand-cyan)"
              className={styles.chart}
            />
          </div>

          <div className={styles.metric}>
            <div className={styles.metricHeader}>
              <span className={styles.metricLabel}>
                <MemoryStick size={14} strokeWidth={1.75} />
                Memory Usage
              </span>
              <span className={styles.metricValue}>
                {Math.round(latest.ramPercent)}%{' '}
                <span className={styles.metricSub}>
                  ({latest.ramUsedGb.toFixed(1)} / {latest.ramTotalGb.toFixed(1)} GB)
                </span>
              </span>
            </div>
            <Sparkline
              data={ramTrend}
              variant="area"
              width={600}
              height={48}
              strokeWidth={1.5}
              color="var(--color-warning)"
              className={styles.chart}
            />
          </div>
        </div>
      )}
    </Card>
  )
}
