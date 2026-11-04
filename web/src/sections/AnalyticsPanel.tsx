import { memo, useId, useState, type KeyboardEvent } from 'react'
import { ApiError, apiPaths } from '@/api/client'
import { isValidSmaN, SMA_MAX_POINTS, useAverage, useSma } from '@/api/queries'
import { isPeriod, PERIODS, type AveragePriceResponse, type SmaResponse } from '@/api/types'
import { FieldLabel } from '@/components/FieldLabel'
import { InfoTip } from '@/components/InfoTip'
import { Skeleton } from '@/components/Skeleton'
import { TitleWithTip } from '@/components/TitleWithTip'
import { formatCount, formatDuration, formatPrice, formatUtc } from '@/lib/format'
import { PERIOD_SECONDS, toUnixSeconds } from '@/lib/sma'
import { N_HINT, parseN, startToIso, type SmaParams } from '@/lib/smaParams'
import { control, selectControl } from '@/lib/styles'
import { cn } from '@/lib/utils'

interface AnalyticsPanelProps {
  symbol: string | undefined
  params: SmaParams
  onParamsChange: (next: SmaParams) => void
}

/**
 * The 24h average and the SMA, side by side with the chart. Every figure is an API answer.
 * Memoised, so hub ticks (several per second) do not re-render it.
 */
export const AnalyticsPanel = memo(function AnalyticsPanel({ symbol, params, onParamsChange }: AnalyticsPanelProps) {
  return (
    <section aria-labelledby="analytics-title" className="glass flex flex-col p-5 sm:p-6">
      <h2 id="analytics-title" className="sr-only">
        Analytics{symbol ? ` for ${symbol}` : ''}
      </h2>
      <AverageCard symbol={symbol} />
      <div aria-hidden="true" className="my-6 h-px bg-glass-border" />
      <SmaCard symbol={symbol} params={params} onParamsChange={onParamsChange} />
      <p className="mt-auto pt-6 text-[0.75rem] leading-relaxed text-ink-faint">
        Every figure on this panel is a live answer from the public API.
      </p>
    </section>
  )
})

const cardTitle = 'text-[0.8125rem] font-medium text-ink-muted'
const bigFigure = 'num mt-2 text-[1.75rem] leading-none font-semibold tracking-[-0.02em]'

function problemText(error: unknown, symbol: string | undefined, noData: string): string {
  if (error instanceof ApiError) {
    if (error.problemSlug === 'no-data') return noData
    if (error.problemSlug === 'unknown-symbol') return `${symbol ?? 'This symbol'} is not monitored.`
    return error.message
  }
  return 'The API did not answer. The next refresh will try again.'
}

function AverageCard({ symbol }: { symbol: string | undefined }) {
  const average = useAverage(symbol)

  return (
    <div>
      <TitleWithTip help="avgPrice">
        <h3 className={cardTitle}>24h average price</h3>
      </TitleWithTip>
      {average.data ? (
        <AverageFigures data={average.data} />
      ) : average.isError ? (
        <p className="mt-2 text-ink-muted">
          {problemText(average.error, symbol, 'No trades collected in the last 24 hours yet.')}
        </p>
      ) : (
        <div className="mt-2 space-y-3">
          <Skeleton className="h-7 w-44" />
          <Skeleton className="h-10 w-full" />
        </div>
      )}
    </div>
  )
}

function AverageFigures({ data }: { data: AveragePriceResponse }) {
  const windowSeconds = data.windowHours * 3600
  const coverage = windowSeconds > 0 ? Math.min(1, data.coveredSeconds / windowSeconds) : 0
  const percent = Math.round(coverage * 1000) / 10

  return (
    <>
      <p className={bigFigure}>
        <data value={data.averagePrice}>{formatPrice(data.averagePrice, data.symbol)}</data>
      </p>
      <dl className="mt-4 grid grid-cols-2 gap-x-4 gap-y-1 text-[0.8125rem]">
        <div>
          <dt className="flex items-center gap-1.5 text-ink-faint">
            Coverage <InfoTip topic="avgCoverage" />
          </dt>
          <dd className="num text-ink">{`${formatDuration(data.coveredSeconds)} of ${data.windowHours}h`}</dd>
        </div>
        <div>
          <dt className="flex items-center gap-1.5 text-ink-faint">
            Trades averaged <InfoTip topic="avgTrades" />
          </dt>
          <dd className="num text-ink">{formatCount(data.sampleCount)}</dd>
        </div>
      </dl>
      <div
        role="meter"
        aria-label="Share of the 24h window with collected trades"
        aria-valuemin={0}
        aria-valuemax={100}
        aria-valuenow={percent}
        aria-valuetext={`${percent}%`}
        className="mt-3 h-1 overflow-hidden rounded-full bg-glass-strong"
      >
        <span className="block h-full rounded-full bg-up" style={{ width: `${coverage * 100}%` }} />
      </div>
    </>
  )
}


function SmaCard({ symbol, params, onParamsChange }: AnalyticsPanelProps) {
  const ids = useId()
  const [nText, setNText] = useState(() => (Number.isNaN(params.n) ? '' : String(params.n)))
  const nValid = isValidSmaN(params.n)
  const startIso = startToIso(params.s)
  const sma = useSma(symbol, params.n, params.p, startIso)

  function setN(text: string) {
    setNText(text)
    onParamsChange({ ...params, n: parseN(text) })
  }

  function stepN(event: KeyboardEvent<HTMLInputElement>) {
    if (event.key !== 'ArrowUp' && event.key !== 'ArrowDown') return
    if (!nValid) return
    event.preventDefault()
    const next = Math.min(SMA_MAX_POINTS, Math.max(1, params.n + (event.key === 'ArrowUp' ? 1 : -1)))
    setN(String(next))
  }

  // Previous answers are kept while the next one loads, but only show them for these parameters.
  const current =
    sma.data?.period === params.p && sma.data.requestedDataPoints === params.n ? sma.data : undefined
  const shown = nValid ? (current ?? sma.data) : undefined
  const stale = shown !== undefined && (shown !== current || sma.isPlaceholderData)

  // Screen readers hear a result once per parameter change (and an error once), never the
  // 30 s background refresh, which may move the value without anyone asking.
  const paramsKey = `${symbol ?? ''}|${params.n}|${params.p}|${params.s}`
  let message: { kind: string; text: string } | null = null
  if (!nValid) message = { kind: 'invalid', text: N_HINT }
  else if (sma.isError && !current)
    message = {
      kind: 'error',
      text: problemText(sma.error, symbol, 'No closes in this window yet.'),
    }
  else if (current && !sma.isPlaceholderData)
    message = {
      kind: 'value',
      text: `SMA over ${current.requestedDataPoints} ${current.period} buckets: ${formatPrice(current.value, current.symbol)}, ${current.actualDataPoints} of ${current.requestedDataPoints} points.`,
    }
  const [announced, setAnnounced] = useState(() => ({ key: `${paramsKey}|value`, text: '' }))
  if (message && announced.key !== `${paramsKey}|${message.kind}`) {
    setAnnounced({ key: `${paramsKey}|${message.kind}`, text: message.text })
  }

  return (
    <div>
      <TitleWithTip help="smaSection">
        <h3 className={cardTitle}>Simple moving average</h3>
      </TitleWithTip>

      <div className="mt-3 grid grid-cols-2 gap-2.5">
        <div>
          <FieldLabel htmlFor={`${ids}-n`} help="smaN">
            Points (n)
          </FieldLabel>
          <input
            id={`${ids}-n`}
            type="text"
            inputMode="numeric"
            autoComplete="off"
            spellCheck={false}
            value={nText}
            onChange={(event) => {
              setN(event.target.value)
            }}
            onKeyDown={stepN}
            aria-invalid={!nValid}
            aria-describedby={nValid ? undefined : `${ids}-n-error`}
            className={cn(control, 'aria-invalid:border-down aria-invalid:text-down')}
          />
        </div>
        <div>
          <FieldLabel htmlFor={`${ids}-p`} help="smaP">
            Period (p)
          </FieldLabel>
          <select
            id={`${ids}-p`}
            value={params.p}
            onChange={(event) => {
              const p = event.target.value
              if (isPeriod(p)) onParamsChange({ ...params, p })
            }}
            className={selectControl}
          >
            {PERIODS.map((period) => (
              <option key={period} value={period}>
                {period}
              </option>
            ))}
          </select>
        </div>
        <div className="col-span-2">
          <FieldLabel htmlFor={`${ids}-s`} help="smaS">
            Start (s), UTC
          </FieldLabel>
          <div className="flex items-center gap-2">
            <input
              id={`${ids}-s`}
              type="datetime-local"
              value={params.s}
              max={new Date().toISOString().slice(0, 16)}
              onChange={(event) => {
                onParamsChange({ ...params, s: event.target.value })
              }}
              aria-describedby={`${ids}-s-hint`}
              className={cn(control, 'min-w-0 flex-1', params.s === '' && 'text-ink-faint')}
            />
            <button
              type="button"
              onClick={() => {
                onParamsChange({ ...params, s: '' })
              }}
              aria-pressed={params.s === ''}
              className="h-9 shrink-0 rounded-(--radius-control) border border-glass-border px-3 text-[0.8125rem] text-ink-muted transition-colors duration-150 hover:text-ink aria-pressed:border-sma/40 aria-pressed:bg-sma-soft aria-pressed:text-ink"
            >
              Now
            </button>
            
          </div>
          <p id={`${ids}-s-hint`} className="mt-1.5 text-[0.75rem] text-ink-faint">
            {params.s === '' ? 'Leave empty for the window that ends now.' : 'The window starts at this bucket.'}
          </p>
        </div>
      </div>

      <p className="sr-only" aria-live="polite">
        {announced.text}
      </p>
      <div className="mt-5">
        {!nValid ? (
          <p id={`${ids}-n-error`} className="text-[0.8125rem] text-down">
            {N_HINT}
          </p>
        ) : shown ? (
          <SmaFigures data={shown} stale={stale} />
        ) : sma.isError ? (
          <p className="text-ink-muted">
            {problemText(sma.error, symbol, 'No closes in this window yet. Try a shorter period or a later start.')}
          </p>
        ) : (
          <div className="space-y-3">
            <Skeleton className="h-7 w-40" />
            <Skeleton className="h-2 w-full" />
            <Skeleton className="h-4 w-52" />
          </div>
        )}
      </div>

      {symbol && nValid && (
        <div className="mt-4 flex items-center gap-1.5 text-[0.6875rem] text-ink-faint">
          <p className="min-w-0 truncate">
            <code>GET {decodeURIComponent(apiPaths.sma(symbol, params.n, params.p, startIso))}</code>
          </p>
          
        </div>
      )}
    </div>
  )
}

function SmaFigures({ data, stale }: { data: SmaResponse; stale: boolean }) {
  const length = PERIOD_SECONDS[data.period]
  const windowStart = toUnixSeconds(data.windowStart)
  const bucketsInWindow = Math.max(0, Math.round((toUnixSeconds(data.windowEnd) - windowStart) / length))
  const missing = Math.max(0, Math.min(bucketsInWindow, data.requestedDataPoints) - data.actualDataPoints)

  let note: string | null = null
  if (bucketsInWindow < data.requestedDataPoints) {
    note = `Only ${bucketsInWindow} ${data.period} ${bucketsInWindow === 1 ? 'bucket has' : 'buckets have'} closed since the start.`
  } else if (missing > 0) {
    note = `${missing} ${missing === 1 ? 'bucket' : 'buckets'} in the window had no trades.`
  }

  return (
    <div className={cn('transition-opacity duration-300', stale && 'opacity-55')}>
      <div className="flex items-center gap-2">
        <p className={cn(bigFigure, 'mt-0 text-sma')}>
          <data value={data.value}>{formatPrice(data.value, data.symbol)}</data>
        </p>
        <InfoTip topic="smaValue" />
      </div>
      <div className="mt-4 flex items-center gap-2">
        <PointsStrip data={data} bucketsInWindow={bucketsInWindow} />
        <InfoTip topic="smaDots" />
      </div>
      <p className="num mt-2.5 flex items-center gap-1.5 text-[0.8125rem] text-ink">
        {`${data.actualDataPoints} / ${data.requestedDataPoints} points`}
        <InfoTip topic="smaPoints" />
      </p>
      <p className="num flex flex-wrap items-center text-[0.8125rem] text-ink-muted">
        <time dateTime={data.windowStart}>{formatUtc(data.windowStart, data.period)}</time>
        <span aria-hidden="true" className="px-1.5 text-ink-faint">
          →
        </span>
        <span className="sr-only"> to </span>
        <time dateTime={data.windowEnd}>{formatUtc(data.windowEnd, data.period)}</time>
        <span className="ml-1">UTC</span>
        <InfoTip topic="smaWindow" className="ml-1.5" />
      </p>
      {note && <p className="mt-1.5 text-[0.75rem] text-ink-faint">{note}</p>}
    </div>
  )
}

/** Above this many buckets the strip becomes a plain bar. */
const MAX_SLOTS = 60

/**
 * The window drawn bucket by bucket: filled where a close exists, hollow where the bucket had no
 * trades, and faint for buckets that have not closed yet. The text next to it says the same.
 */
function PointsStrip({ data, bucketsInWindow }: { data: SmaResponse; bucketsInWindow: number }) {
  const requested = data.requestedDataPoints
  if (requested > MAX_SLOTS || data.dataPoints.length === 0) {
    const share = requested > 0 ? data.actualDataPoints / requested : 0
    return (
      <div aria-hidden="true" className="h-1.5 min-w-0 flex-1 overflow-hidden rounded-full bg-glass-strong">
        <span className="block h-full rounded-full bg-sma" style={{ width: `${Math.min(1, share) * 100}%` }} />
      </div>
    )
  }

  const length = PERIOD_SECONDS[data.period]
  const start = toUnixSeconds(data.windowStart)
  const present = new Set(data.dataPoints.map((point) => toUnixSeconds(point.bucketStart)))
  const slots = Array.from({ length: requested }, (_, i) => {
    if (i >= bucketsInWindow) return 'future' as const
    return present.has(start + i * length) ? ('filled' as const) : ('empty' as const)
  })

  return (
    <div aria-hidden="true" className="flex h-2.5 min-w-0 flex-1 gap-[3px]">
      {slots.map((slot, i) => (
        <span
          key={i}
          className={cn(
            'min-w-0 flex-1 rounded-[2px]',
            slot === 'filled' && 'bg-sma',
            slot === 'empty' && 'border border-sma/45',
            slot === 'future' && 'bg-glass-strong',
          )}
        />
      ))}
    </div>
  )
}
