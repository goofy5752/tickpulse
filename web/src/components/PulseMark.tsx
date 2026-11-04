import { useId } from 'react'

/** The TickPulse mark: a pulse line running from the up teal into the SMA violet. */
export function PulseMark({ className }: { className?: string }) {
  const gradientId = useId()
  return (
    <svg viewBox="0 0 32 32" aria-hidden="true" className={className}>
      <defs>
        <linearGradient id={gradientId} x1="0" x2="1" y1="0" y2="0">
          <stop offset="0" stopColor="var(--color-up)" />
          <stop offset="1" stopColor="var(--color-sma)" />
        </linearGradient>
      </defs>
      <polyline
        points="3,18 9,18 12.5,8 17,26 20.5,13 23,18 29,18"
        fill="none"
        stroke={`url(#${gradientId})`}
        strokeWidth="2.6"
        strokeLinecap="round"
        strokeLinejoin="round"
      />
    </svg>
  )
}
