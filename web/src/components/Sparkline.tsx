import { useId } from 'react'
import { cn } from '@/lib/utils'

interface SparklineProps {
  values: readonly number[]
  /** Accessible summary; the line itself is decorative. */
  label: string
  className?: string
  /** For series that are not prices (e.g. throughput): one quiet colour instead of up/down. */
  neutral?: boolean
}

const WIDTH = 120
const HEIGHT = 36
const PAD = 3

/**
 * A minimal trend line with a soft fill and an end dot. It colours itself by its own direction
 * (last value vs first), so it never claims a trend the data does not show.
 */
export function Sparkline({ values, label, className, neutral = false }: SparklineProps) {
  const gradientId = useId()

  if (values.length < 2) {
    return (
      <svg viewBox={`0 0 ${WIDTH} ${HEIGHT}`} role="img" aria-label={label} className={cn('text-ink-faint', className)}>
        <line x1="0" x2={WIDTH} y1={HEIGHT / 2} y2={HEIGHT / 2} stroke="currentColor" strokeOpacity="0.35" strokeDasharray="2 4" />
      </svg>
    )
  }

  let min = Infinity
  let max = -Infinity
  for (const v of values) {
    if (v < min) min = v
    if (v > max) max = v
  }
  const range = max - min || Math.abs(max) * 0.001 || 1
  const step = WIDTH / (values.length - 1)
  const points = values.map((v, i) => {
    const x = i * step
    const y = PAD + (1 - (v - min) / range) * (HEIGHT - PAD * 2)
    return [x, y] as const
  })
  const line = points.map(([x, y]) => `${x.toFixed(2)},${y.toFixed(2)}`).join(' ')
  const area = `0,${HEIGHT} ${line} ${WIDTH},${HEIGHT}`
  const last = points.at(-1) ?? [WIDTH, HEIGHT / 2]
  const first = values[0] ?? 0
  const end = values.at(-1) ?? 0
  const tone = neutral ? 'text-ink-muted' : end > first ? 'text-up' : end < first ? 'text-down' : 'text-ink-faint'

  return (
    <span role="img" aria-label={label} className={cn('relative block', tone, className)}>
      <svg
        viewBox={`0 0 ${WIDTH} ${HEIGHT}`}
        preserveAspectRatio="none"
        aria-hidden="true"
        className="block size-full overflow-visible"
      >
        <defs>
          <linearGradient id={gradientId} x1="0" x2="0" y1="0" y2="1">
            <stop offset="0" stopColor="currentColor" stopOpacity="0.22" />
            <stop offset="1" stopColor="currentColor" stopOpacity="0" />
          </linearGradient>
        </defs>
        <polygon points={area} fill={`url(#${gradientId})`} />
        <polyline
          points={line}
          fill="none"
          stroke="currentColor"
          strokeWidth="1.5"
          strokeLinejoin="round"
          strokeLinecap="round"
          vectorEffect="non-scaling-stroke"
        />
      </svg>
      {/* An HTML dot stays round however the line is stretched. */}
      <span
        aria-hidden="true"
        className="absolute size-[5px] -translate-1/2 rounded-full bg-current shadow-[0_0_0_3px_color-mix(in_oklab,currentColor_22%,transparent)]"
        style={{ left: `${(last[0] / WIDTH) * 100}%`, top: `${(last[1] / HEIGHT) * 100}%` }}
      />
    </span>
  )
}
