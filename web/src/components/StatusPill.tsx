import type { SystemStatsResponse } from '@/api/types'
import type { HubStatus } from '@/hooks/useMarketHub'
import { formatLag } from '@/lib/format'
import { cn } from '@/lib/utils'

type Tone = 'live' | 'wait' | 'down'

interface PillState {
  tone: Tone
  label: string
  /** What a screen reader hears; the visible label is shorter on small screens. */
  description: string
}

/** Lag above this reads as a warning even while the feed is connected. */
export const SLOW_LAG_MS = 5_000

function describe(hub: HubStatus, stats: SystemStatsResponse | null): PillState {
  if (hub === 'reconnecting') {
    return { tone: 'wait', label: 'Reconnecting', description: 'Reconnecting to the live feed' }
  }
  if (!stats) {
    return hub === 'disconnected'
      ? { tone: 'down', label: 'Offline', description: 'The dashboard cannot reach the API' }
      : { tone: 'wait', label: 'Connecting', description: 'Connecting to the live feed' }
  }
  switch (stats.wsStatus) {
    case 'Connected':
      return { tone: 'live', label: 'Live', description: 'Binance stream live' }
    case 'Connecting':
      return { tone: 'wait', label: 'Connecting', description: 'The API is connecting to Binance' }
    case 'Standby':
      return { tone: 'wait', label: 'Standby', description: 'This API instance is on standby' }
    case 'Disconnected':
      return { tone: 'down', label: 'Feed down', description: 'The Binance stream is disconnected' }
  }
}

const toneStyles: Record<Tone, { pill: string; dot: string }> = {
  live: { pill: 'border-up/35 bg-up-soft', dot: 'bg-up status-dot-live' },
  wait: { pill: 'border-glass-border bg-glass-strong', dot: 'bg-ink-faint status-dot-wait' },
  down: { pill: 'border-down/40 bg-down-soft', dot: 'bg-down' },
}

interface StatusPillProps {
  hubStatus: HubStatus
  stats: SystemStatsResponse | null
}

/**
 * The ingestion status at a glance: is the Binance stream live, and how far behind are we.
 * Below 640 px the pill drops the "Binance" prefix and the word "lag" to fit the compact nav.
 */
export function StatusPill({ hubStatus, stats }: StatusPillProps) {
  const state = describe(hubStatus, stats)
  const showLag = state.tone === 'live' && stats !== null
  const slow = showLag && stats.ingestLagMs > SLOW_LAG_MS
  const styles = toneStyles[state.tone]

  return (
    <p
      className={cn(
        'inline-flex h-9 shrink-0 items-center gap-2 rounded-full border pr-3.5 pl-3 text-[0.8125rem] whitespace-nowrap',
        styles.pill,
      )}
    >
      <span aria-hidden="true" className={cn('relative size-2 rounded-full', styles.dot)} />
      {/* Announce state changes only; the lag ticks every 2 s and would be noise. */}
      <span className="sr-only" role="status">
        {state.description}
      </span>
      <span aria-hidden="true" className="font-medium text-ink">
        {state.tone === 'live' ? (
          <>
            <span className="sm:hidden">Live</span>
            <span className="hidden sm:inline">Binance live</span>
          </>
        ) : (
          state.label
        )}
      </span>
      {showLag && (
        <>
          <span aria-hidden="true" className="h-3.5 w-px bg-glass-border" />
          <span className={cn('num text-ink-muted', slow && 'text-down')}>
            <span className="sr-only">ingest lag </span>
            {formatLag(stats.ingestLagMs)}
            <span aria-hidden="true" className="hidden sm:inline">
              {' '}
              lag
            </span>
          </span>
        </>
      )}
    </p>
  )
}
