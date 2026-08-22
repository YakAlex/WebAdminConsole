import { useId, useMemo } from 'react'

export interface SparklineProps {
  data: number[]
  width?: number
  height?: number
  color?: string
  variant?: 'line' | 'area'
  strokeWidth?: number
  className?: string
}

function buildPath(data: number[], width: number, height: number, padding: number) {
  const min = Math.min(...data)
  const max = Math.max(...data)
  const range = max - min || 1
  const innerHeight = height - padding * 2
  const step = data.length > 1 ? (width - padding * 2) / (data.length - 1) : 0

  return data.map((value, index) => {
    const x = padding + index * step
    const y = padding + innerHeight - ((value - min) / range) * innerHeight
    return { x, y }
  })
}

/**
 * Thin inline chart with no third-party libraries (§11: "don't build a
 * big TradingView-style chart"). One component — Uptime KPI, the Uptime
 * by Device trend column — all consume the same primitive.
 */
export function Sparkline({
  data,
  width = 120,
  height = 32,
  color = 'var(--color-brand-cyan)',
  variant = 'line',
  strokeWidth = 1.5,
  className,
}: SparklineProps) {
  const gradientId = useId()

  const { linePath, areaPath } = useMemo(() => {
    if (data.length < 2) return { linePath: '', areaPath: '' }

    const points = buildPath(data, width, height, strokeWidth)
    const line = points.map((p, i) => `${i === 0 ? 'M' : 'L'}${p.x.toFixed(2)},${p.y.toFixed(2)}`).join(' ')
    const area = `${line} L${points[points.length - 1]!.x.toFixed(2)},${height} L${points[0]!.x.toFixed(2)},${height} Z`

    return { linePath: line, areaPath: area }
  }, [data, width, height, strokeWidth])

  if (!linePath) return null

  return (
    <svg
      className={className}
      width={width}
      height={height}
      viewBox={`0 0 ${width} ${height}`}
      preserveAspectRatio="none"
      fill="none"
      role="img"
      aria-hidden="true"
    >
      {variant === 'area' && (
        <>
          <defs>
            <linearGradient id={gradientId} x1="0" y1="0" x2="0" y2="1">
              <stop offset="0%" stopColor={color} stopOpacity="0.28" />
              <stop offset="100%" stopColor={color} stopOpacity="0" />
            </linearGradient>
          </defs>
          <path d={areaPath} fill={`url(#${gradientId})`} />
        </>
      )}
      <path d={linePath} stroke={color} strokeWidth={strokeWidth} strokeLinejoin="round" strokeLinecap="round" />
    </svg>
  )
}
