import { lazy, memo, Suspense, useCallback, useId, useState, type ReactNode } from 'react'
import { ApiError } from '@/api/client'
import { isValidSmaN, SMA_MAX_POINTS, useCandles, useSma } from '@/api/queries'
import { PERIODS, type CandleResponse, type LivePrice, type Period } from '@/api/types'
import { InfoTip } from '@/components/InfoTip'
import type { ChartType } from '@/components/PriceChart'
import { Skeleton } from '@/components/Skeleton'
import { formatPrice, splitSymbol } from '@/lib/format'
import { SECTION_IDS } from '@/lib/links'
import { toUnixSeconds } from '@/lib/sma'
import { startToIso, type SmaParams } from '@/lib/smaParams'
import { cn } from '@/lib/utils'

// The charting library is the largest dependency; load it after the first paint.
const PriceChart = lazy(() => import('@/components/PriceChart').then((m) => ({ default: m.PriceChart })))

/** How far back each period's chart reaches by default (6 h, 24 h, 7 d, 1 y, 5 y). */
const CANDLE_LIMITS: Record<Period, number> = { '1m': 360, '5m': 288, '30m': 336, '1d': 365, '1w': 260 }

/** Enough candles for the default range, and for the SMA window plus some context before it. */
function candleLimit(p: Period, n: number): number {
  const forWindow = isValidSmaN(n) ? n + Math.ceil(n / 4) : 0
  return Math.min(SMA_MAX_POINTS, Math.max(CANDLE_LIMITS[p], forWindow))
}

/** The viewer's last choice of candles or line, remembered per browser. */
export const CHART_TYPE_STORAGE_KEY = 'tickpulse.chartType'

function storedChartType(): ChartType {
  try {
    return localStorage.getItem(CHART_TYPE_STORAGE_KEY) === 'line' ? 'line' : 'candles'
  } catch {
    return 'candles'
  }
}

function useChartType() {
  const [type, setType] = useState<ChartType>(storedChartType)
  const choose = useCallback((next: ChartType) => {
    setType(next)
    try {
      localStorage.setItem(CHART_TYPE_STORAGE_KEY, next)
    } catch {
      // Storage can be unavailable (private mode, blocked site data); the choice still applies.
    }
  }, [])
  return [type, choose] as const
}

interface ChartPanelProps {
  symbol: string | undefined
  params: SmaParams
  onParamsChange: (next: SmaParams) => void
  live: LivePrice | undefined
  /** Latest price from the symbols list, shown until the first live trade arrives. */
  fallbackPrice: number | null
}

interface Settled {
  symbol: string
  period: Period
  candles: CandleResponse[]
}

/** Memoised: hub ticks for other symbols do not reach it; only its own live price does. */
export const ChartPanel = memo(function ChartPanel({ symbol, params, onParamsChange, live, fallbackPrice }: ChartPanelProps) {
  const limit = candleLimit(params.p, params.n)
  const candles = useCandles(symbol, params.p, limit)
  const sma = useSma(symbol, params.n, params.p, startToIso(params.s))
  const [chartType, setChartType] = useChartType()

  // Keep drawing the last complete answer (dimmed) while the next symbol or period loads, so the
  // chart stays mounted, and never mix one period's candles with another period's bucket width.
  const [settled, setSettled] = useState<Settled | null>(null)
  if (symbol && candles.data && !candles.isPlaceholderData && settled?.candles !== candles.data) {
    setSettled({ symbol, period: params.p, candles: candles.data })
  }
  const view = settled
  const stale = view !== null && (view.symbol !== symbol || view.period !== params.p || candles.isPlaceholderData)
  /** The candles for the current symbol and period failed and there is nothing current to show. */
  const failed = candles.isError && candles.data === undefined
  const errorDetail =
    candles.error instanceof ApiError ? candles.error.message : 'The API did not answer. Check the connection and try again.'
  const retry = () => {
    void candles.refetch()
  }

  // Only an answer for exactly what is drawn: no previous-parameter placeholder at full strength.
  const smaForChart =
    view &&
    !stale &&
    !sma.isPlaceholderData &&
    sma.data?.symbol === view.symbol &&
    sma.data.period === view.period &&
    sma.data.requestedDataPoints === params.n
      ? sma.data
      : undefined

  const { base, quote } = splitSymbol(symbol ?? '')
  const lastClose = view?.candles.at(-1)?.close
  const headlinePrice = live?.price ?? fallbackPrice ?? lastClose ?? null

  return (
    <section id={SECTION_IDS.chart} aria-labelledby="chart-title" className="glass flex min-w-0 flex-col p-4 sm:p-6">
      <div className="flex flex-wrap items-center justify-between gap-x-4 gap-y-3">
        <div className="flex min-w-0 items-center gap-3">
          <h2 id="chart-title" className="text-[1.25rem] leading-none font-semibold tracking-tight">
            {symbol ? (
              <>
                {base}
                {quote && <span className="font-medium text-ink-faint">/{quote}</span>}
              </>
            ) : (
              'Price chart'
            )}
          </h2>
          {symbol && headlinePrice !== null && (
            <p className="num text-[1.25rem] leading-none text-ink-muted">
              <span className="sr-only">Last price </span>
              {formatPrice(headlinePrice, symbol)}
            </p>
          )}
          <InfoTip topic="chartSection" />
        </div>
        <div className="flex items-center gap-2">
          <ChartTypeToggle value={chartType} onChange={setChartType} />
          <PeriodPicker
            value={params.p}
            onChange={(p) => {
              onParamsChange({ ...params, p })
            }}
          />
          <InfoTip topic="chartPeriod" />
        </div>
      </div>

      <div className="relative mt-4 flex h-[19rem] sm:h-[23rem] lg:h-auto lg:min-h-[26rem] lg:flex-1">
        {view && view.candles.length > 0 ? (
          <Suspense fallback={<ChartSkeleton />}>
            <PriceChart
              symbol={view.symbol}
              candles={view.candles}
              period={view.period}
              type={chartType}
              sma={smaForChart}
              live={stale ? undefined : live}
              fitKey={`${view.symbol}|${view.period}`}
              className={cn('flex-1 transition-opacity duration-300', stale && 'opacity-45')}
            />
          </Suspense>
        ) : failed ? (
          <EmptyState title={`Could not load the ${params.p} candles.`} action={<RetryButton onClick={retry} />}>
            {errorDetail}
          </EmptyState>
        ) : view && !stale ? (
          <EmptyState title={`No ${view.period} candles for ${view.symbol} yet.`}>
            Trades collected from now on show up here within a minute.
          </EmptyState>
        ) : (
          <ChartSkeleton />
        )}
        {failed && view && view.candles.length > 0 ? (
          <div
            role="alert"
            className="absolute top-8 left-1/2 flex w-[min(24rem,calc(100%-2rem))] -translate-x-1/2 flex-col items-center gap-2 rounded-(--radius-control) border border-down/40 bg-glass-strong p-4 text-center backdrop-blur-md"
          >
            <p className="font-medium text-ink">{`Could not load the ${params.p} candles for ${symbol ?? 'this symbol'}.`}</p>
            <p className="text-[0.8125rem] text-ink-muted">{errorDetail}</p>
            <RetryButton onClick={retry} />
          </div>
        ) : (
          stale &&
          !failed && (
            <p
              role="status"
              className="absolute top-8 left-1/2 -translate-x-1/2 rounded-full border border-glass-border bg-glass-strong px-3 py-1 text-[0.75rem] whitespace-nowrap text-ink-muted backdrop-blur-md"
            >
              {`Loading ${symbol ?? ''} ${params.p} candles`}
            </p>
          )
        )}
      </div>

      <Legend
        period={view?.period ?? params.p}
        type={chartType}
        n={smaForChart?.requestedDataPoints}
        note={windowNote(view, smaForChart)}
      />
    </section>
  )
})

function RetryButton({ onClick }: { onClick: () => void }) {
  return (
    <button
      type="button"
      onClick={onClick}
      className="mt-1 rounded-full border border-glass-border bg-glass px-3.5 py-1.5 text-[0.8125rem] font-medium text-ink transition-colors duration-150 hover:bg-glass-strong"
    >
      Try again
    </button>
  )
}

function windowNote(view: Settled | null, sma: { windowStart: string; windowEnd: string } | undefined): string | null {
  const first = view?.candles[0]
  const last = view?.candles.at(-1)
  if (!sma || !first || !last) return null
  const start = toUnixSeconds(sma.windowStart)
  const end = toUnixSeconds(sma.windowEnd)
  if (end <= toUnixSeconds(first.bucketStart) || start > toUnixSeconds(last.bucketStart)) {
    return 'The SMA window lies outside the candles shown.'
  }
  if (start < toUnixSeconds(first.bucketStart)) return 'The SMA window starts before the first candle shown.'
  return null
}

function PeriodPicker({ value, onChange }: { value: Period; onChange: (p: Period) => void }) {
  const name = useId()
  return (
    <fieldset className="flex rounded-full border border-glass-border bg-glass p-0.5">
      <legend className="sr-only">Candle period</legend>
      {PERIODS.map((period) => (
        <label key={period} className="relative">
          <input
            type="radio"
            name={name}
            value={period}
            checked={value === period}
            onChange={() => {
              onChange(period)
            }}
            className="peer sr-only"
          />
          <span className="num block cursor-pointer rounded-full px-3 py-1 text-[0.8125rem] text-ink-muted transition-colors duration-150 peer-checked:bg-glass-strong peer-checked:font-medium peer-checked:text-ink peer-checked:shadow-[inset_0_0_0_1px_var(--color-glass-border)] peer-focus-visible:outline-2 peer-focus-visible:outline-offset-1 peer-focus-visible:outline-sma hover:text-ink">
            {period}
          </span>
        </label>
      ))}
    </fieldset>
  )
}

const CHART_TYPES: readonly { value: ChartType; label: string; icon: ReactNode }[] = [
  {
    value: 'candles',
    label: 'Candles',
    icon: (
      <svg viewBox="0 0 16 16" aria-hidden="true" className="size-4" fill="none">
        <path d="M4.5 2.5v11M11.5 1.5v10" stroke="currentColor" strokeWidth="1.2" strokeLinecap="round" />
        <rect x="2.75" y="5" width="3.5" height="6" rx="0.8" fill="currentColor" />
        <rect x="9.75" y="3.5" width="3.5" height="5" rx="0.8" stroke="currentColor" strokeWidth="1.2" />
      </svg>
    ),
  },
  {
    value: 'line',
    label: 'Line',
    icon: (
      <svg viewBox="0 0 16 16" aria-hidden="true" className="size-4" fill="none">
        <path
          d="M1.5 11.5 5 7.5l3 2.5 6.5-6.5"
          stroke="currentColor"
          strokeWidth="1.5"
          strokeLinecap="round"
          strokeLinejoin="round"
        />
      </svg>
    ),
  },
]

/** Candles or a line of closes, as an icon switch next to the periods. */
function ChartTypeToggle({ value, onChange }: { value: ChartType; onChange: (type: ChartType) => void }) {
  const name = useId()
  return (
    <fieldset className="flex rounded-full border border-glass-border bg-glass p-0.5">
      <legend className="sr-only">Chart type</legend>
      {CHART_TYPES.map((option) => (
        <label key={option.value} className="relative" title={option.label}>
          <input
            type="radio"
            name={name}
            value={option.value}
            checked={value === option.value}
            onChange={() => {
              onChange(option.value)
            }}
            aria-label={option.label}
            className="peer sr-only"
          />
          <span className="grid h-7 w-8 cursor-pointer place-items-center rounded-full text-ink-muted transition-colors duration-150 peer-checked:bg-glass-strong peer-checked:text-ink peer-checked:shadow-[inset_0_0_0_1px_var(--color-glass-border)] peer-focus-visible:outline-2 peer-focus-visible:outline-offset-1 peer-focus-visible:outline-sma hover:text-ink">
            {option.icon}
          </span>
        </label>
      ))}
    </fieldset>
  )
}

function Legend({
  period,
  type,
  n,
  note,
}: {
  period: Period
  type: ChartType
  n: number | undefined
  note: string | null
}) {
  return (
    <div className="mt-4 flex flex-wrap items-center gap-x-5 gap-y-2 text-[0.75rem] text-ink-muted">
      <ul className="contents">
        <li className="flex items-center gap-2">
          {type === 'line' ? (
            <span aria-hidden="true" className="h-0.5 w-4 rounded-full bg-ink-muted" />
          ) : (
            <span aria-hidden="true" className="flex items-end gap-[2px]">
              <span className="h-2.5 w-[5px] rounded-[1px] bg-up" />
              <span className="h-3.5 w-[5px] rounded-[1px] bg-down" />
            </span>
          )}
          {type === 'line' ? `${period} closes` : `${period} candles`}
          <InfoTip topic="chartPrice" className="-ml-0.5" />
        </li>
        {n !== undefined && (
          <>
            <li className="flex items-center gap-2">
              <span aria-hidden="true" className="h-0.5 w-4 rounded-full bg-sma" />
              SMA, n = {n}
              <InfoTip topic="chartSma" className="-ml-0.5" />
            </li>
            <li className="flex items-center gap-2">
              <span aria-hidden="true" className="h-3.5 w-4 rounded-[3px] border border-dashed border-sma/60 bg-sma-soft" />
              Calculation window
              <InfoTip topic="chartWindow" className="-ml-0.5" />
            </li>
          </>
        )}
      </ul>
      {note && <p className="text-ink-faint">{note}</p>}
    </div>
  )
}

function ChartSkeleton() {
  return (
    <div className="flex flex-1 flex-col gap-3" aria-hidden="true">
      <Skeleton className="h-4 w-72 max-w-full" />
      <Skeleton className="flex-1" />
    </div>
  )
}

function EmptyState({ title, children, action }: { title: string; children: ReactNode; action?: ReactNode }) {
  return (
    <div className="flex flex-1 flex-col items-center justify-center rounded-(--radius-control) border border-dashed border-glass-border p-6 text-center">
      <p className="font-medium text-ink">{title}</p>
      <p className="mt-1 max-w-[46ch] text-ink-muted">{children}</p>
      {action}
    </div>
  )
}
