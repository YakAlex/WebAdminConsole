import type { ReactNode } from 'react'
import styles from './HealthRing.module.scss'

export interface HealthRingProps {
  percent: number
  size?: number
  strokeWidth?: number
  color?: string
  children?: ReactNode
}

/**
 * Circular progress indicator (§7: "Health Ring", 120–140px). Independent
 * of System Health — usable on any future page (Ping global health,
 * Backup success ring, etc.).
 */
export function HealthRing({ percent, size = 132, strokeWidth = 8, color = 'var(--color-success)', children }: HealthRingProps) {
  const radius = (size - strokeWidth) / 2
  const circumference = 2 * Math.PI * radius
  const offset = circumference * (1 - Math.min(Math.max(percent, 0), 100) / 100)

  return (
    <div className={styles.wrapper} style={{ width: size, height: size }}>
      <svg width={size} height={size} viewBox={`0 0 ${size} ${size}`}>
        <circle
          className={styles.track}
          cx={size / 2}
          cy={size / 2}
          r={radius}
          fill="none"
          strokeWidth={strokeWidth}
        />
        <circle
          className={styles.progress}
          cx={size / 2}
          cy={size / 2}
          r={radius}
          fill="none"
          stroke={color}
          strokeWidth={strokeWidth}
          strokeLinecap="round"
          strokeDasharray={circumference}
          strokeDashoffset={offset}
          transform={`rotate(-90 ${size / 2} ${size / 2})`}
        />
      </svg>
      <div className={styles.center}>{children}</div>
    </div>
  )
}
