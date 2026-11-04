import { memo, useId, useState, type ReactNode } from 'react'
import type { SystemStatsResponse, WsStatus } from '@/api/types'
import { InfoTip } from '@/components/InfoTip'
import { Skeleton } from '@/components/Skeleton'
import { Sparkline } from '@/components/Sparkline'
import { SLOW_LAG_MS } from '@/components/StatusPill'
import { TitleWithTip } from '@/components/TitleWithTip'
import type { HelpKey } from '@/content/help'
import type { HubStatus } from '@/hooks/useMarketHub'
import { useNow } from '@/hooks/useNow'
import { formatCompact, formatCount, formatDuration, formatLag } from '@/lib/format'
import { SECTION_IDS } from '@/lib/links'
import { sectionLead, sectionTitle } from '@/lib/styles'
import { cn } from '@/lib/utils'

/** Pushes kept for the ticks-per-second trend: a minute at the hub's 2 s cadence. */
const HISTORY = 30

const WS_LABEL: Record<WsStatus, { text: string; className: string }> = {
  Connected: { text: 'Binance connected', className: 'border-up/30 bg-up-soft text-up' },
  Connecting: { text: 'Connecting to Binance', className: 'border-glass-border bg-glass-strong text-ink-muted' },
  Standby: { text: 'Standby', className: 'border-glass-border bg-glass-strong text-ink-muted' },
  Disconnected: { text: 'Binance disconnected', className: 'border-down/35 bg-down-soft text-down' },
}

const utcTime = new Intl.DateTimeFormat('en-US', {
  timeZone: 'UTC',
  month: 'short',
  day: 'numeric',
  hour: '2-digit',
  minute: '2-digit',
  hourCycle: 'h23',
})

interface SystemPanelProps {
  /** The hub's latest push, or the HTTP fallback while the hub is down. */
  stats: SystemStatsResponse | null
  hubStatus: HubStatus
}

/** Ingestion health: throughput, lag, storage, uptime and the WebSocket rotation schedule. */
export const SystemPanel = memo(function SystemPanel({ stats, hubStatus }: SystemPanelProps) {
  const ids = useId()
  const now = useNow(5_000)

  // Each new stats object is one sample (state derived during render, React's pattern for "on change").
  const [history, setHistory] = useState<{ last: SystemStatsResponse | null; values: number[] }>({ last: null, values: [] })
  if (stats && stats !== history.last) {
    setHistory({ last: stats, values: [...history.values, stats.ticksPerSecond].slice(-HISTORY) })
  }

  const live = hubStatus === 'connected'

  return (
    <section id={SECTION_IDS.system} aria-labelledby={`${ids}-title`} className="glass flex flex-col p-5 sm:p-6">
      <div className="flex flex-wrap items-start justify-between gap-x-4 gap-y-2">
        <div>
          <TitleWithTip help="systemSection">
            <h2 id={`${ids}-title`} className={sectionTitle}>
              System
            </h2>
          </TitleWithTip>
          <p className={sectionLead}>How the ingestion pipeline is doing right now.</p>
        </div>
        {stats && (
          <p className="flex flex-wrap gap-1.5">
            <span className={cn('rounded-full border px-2 py-0.5 text-[0.75rem] whitespace-nowrap', WS_LABEL[stats.wsStatus].className)}>
              {WS_LABEL[stats.wsStatus].text}
            </span>
            <span className="rounded-full border border-glass-border px-2 py-0.5 text-[0.75rem] whitespace-nowrap text-ink-muted">
              {stats.isLeader ? 'Leader' : 'Follower'}
            </span>
          </p>
        )}
      </div>

      {stats ? (
        <>
          <dl className="mt-5 grid grid-cols-2 gap-x-4 gap-y-5">
            <Metric label="Ticks per second" help="systemTicks">
              <span className="flex items-end gap-3">
                <data value={stats.ticksPerSecond}>{stats.ticksPerSecond.toFixed(1)}</data>
                <Sparkline
                  neutral
                  values={history.values}
                  label={`Ticks per second over the last ${history.values.length} updates`}
                  className="mb-1 h-6 w-full max-w-24"
                />
              </span>
            </Metric>
            <Metric label="Ingest lag" help="systemLag">
              <span className={cn(stats.ingestLagMs > SLOW_LAG_MS && 'text-down')}>{formatLag(stats.ingestLagMs)}</span>
            </Metric>
            <Metric label="Ticks stored">
              <span title={`About ${formatCount(stats.storedTicksApprox)} ticks`}>{formatCompact(stats.storedTicksApprox)}</span>
            </Metric>
            <Metric label="Uptime">{formatDuration(stats.uptimeSeconds)}</Metric>
          </dl>

          <Rotation stats={stats} now={now} />

          <p className="mt-auto pt-5 text-[0.75rem] text-ink-faint">
            {live ? 'Streamed over the live connection.' : 'Polled over HTTP while the live connection is down.'}{' '}
            {stats.activeSymbols} {stats.activeSymbols === 1 ? 'symbol' : 'symbols'} active, API {stats.version}.
          </p>
        </>
      ) : hubStatus === 'disconnected' ? (
        <p className="mt-5 text-ink-muted">The API is not answering. Figures appear here as soon as it is back.</p>
      ) : (
        <div aria-hidden="true" className="mt-5 grid grid-cols-2 gap-5">
          {[0, 1, 2, 3].map((i) => (
            <div key={i} className="space-y-2">
              <Skeleton className="h-3 w-24" />
              <Skeleton className="h-6 w-20" />
            </div>
          ))}
        </div>
      )}
    </section>
  )
})

function Metric({ label, help, children }: { label: string; help?: HelpKey; children: ReactNode }) {
  return (
    <div className="min-w-0">
      <dt className="flex items-center gap-1.5 text-[0.75rem] text-ink-faint">
        {label}
        {help && <InfoTip topic={help} />}
      </dt>
      <dd className="num mt-1 text-[1.375rem] leading-none font-semibold tracking-[-0.02em] text-ink">{children}</dd>
    </div>
  )
}

function Rotation({ stats, now }: { stats: SystemStatsResponse; now: number }) {
  const since = stats.connectedSince ? Date.parse(stats.connectedSince) : Number.NaN
  const next = stats.nextRotation ? Date.parse(stats.nextRotation) : Number.NaN
  const reconnect = stats.lastReconnect ? Date.parse(stats.lastReconnect) : Number.NaN
  const lifetime = next - since
  const hasSchedule = Number.isFinite(lifetime) && lifetime > 0
  const percent = hasSchedule ? Math.round(Math.min(1, Math.max(0, (now - since) / lifetime)) * 100) : 0
  const hours = hasSchedule ? Math.round(lifetime / 3_600_000) : 0

  return (
    <div className="mt-6 rounded-(--radius-control) border border-glass-border bg-glass px-4 py-3.5">
      <div className="flex flex-wrap items-baseline justify-between gap-x-3 gap-y-1">
        <TitleWithTip help="systemConnection">
          <h3 className="text-[0.8125rem] font-medium text-ink">Binance connection</h3>
        </TitleWithTip>
        {hasSchedule && <span className="text-[0.75rem] text-ink-faint">{`Renewed every ${hours} h, before Binance's 24 h cut-off`}</span>}
      </div>

      {hasSchedule && (
        <div
          role="meter"
          aria-label="Connection lifetime used before the next rotation"
          aria-valuemin={0}
          aria-valuemax={100}
          aria-valuenow={percent}
          aria-valuetext={`${percent}%`}
          className="mt-3 h-1 overflow-hidden rounded-full bg-glass-strong"
        >
          <span className="block h-full rounded-full bg-ink-muted" style={{ width: `${percent}%` }} />
        </div>
      )}

      <dl className="mt-3 grid grid-cols-1 gap-2 text-[0.8125rem] sm:grid-cols-2">
        <div className="flex justify-between gap-3 sm:block">
          <dt className="flex items-center gap-1.5 text-ink-faint">
            Last reconnect
          </dt>
          <dd className="num text-ink">
            {Number.isFinite(reconnect) ? (
              <time dateTime={stats.lastReconnect ?? undefined} title={`${utcTime.format(reconnect)} UTC`}>
                {`${formatDuration((now - reconnect) / 1000)} ago`}
              </time>
            ) : (
              'None yet'
            )}
          </dd>
        </div>
        <div className="flex justify-between gap-3 sm:block">
          <dt className="flex items-center gap-1.5 text-ink-faint">
            Next rotation <InfoTip topic="systemNextRotation" />
          </dt>
          <dd className="num text-ink">
            {Number.isFinite(next) ? (
              <time dateTime={stats.nextRotation ?? undefined} title={`${utcTime.format(next)} UTC`}>
                {next > now ? `in ${formatDuration((next - now) / 1000)}` : 'Due now'}
              </time>
            ) : (
              'Not scheduled'
            )}
          </dd>
        </div>
      </dl>
    </div>
  )
}
