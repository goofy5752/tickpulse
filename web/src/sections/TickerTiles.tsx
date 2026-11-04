import { memo, useMemo, useState } from 'react'
import { useCandles } from '@/api/queries'
import type { LivePrice, SymbolResponse } from '@/api/types'
import { Skeleton } from '@/components/Skeleton'
import { Sparkline } from '@/components/Sparkline'
import { usePrefersReducedMotion } from '@/hooks/usePrefersReducedMotion'
import { formatDuration, formatPercent, formatPrice, splitSymbol } from '@/lib/format'
import { SECTION_IDS } from '@/lib/links'
import { toUnixSeconds } from '@/lib/sma'
import { cn } from '@/lib/utils'

const SPARK_POINTS = 60

interface TickerTilesProps {
  symbols: SymbolResponse[] | undefined
  isLoading: boolean
  error: Error | null
  prices: Record<string, LivePrice>
  selected: string | undefined
  /** Keep it stable (e.g. a state setter): the tiles are memoised on it. */
  onSelect: (symbol: string) => void
}

/**
 * One tile per monitored symbol, scrolling sideways when they outgrow the row. Tiles are memoised,
 * so a hub tick re-renders only the tile whose price moved.
 */
export const TickerTiles = memo(function TickerTiles({ symbols, isLoading, error, prices, selected, onSelect }: TickerTilesProps) {
  return (
    <section id={SECTION_IDS.markets} aria-labelledby="markets-title">
      <h2 id="markets-title" className="sr-only">
        Live prices
      </h2>
      <ul className="tile-rail -mx-4 flex snap-x snap-mandatory scroll-px-4 gap-3 overflow-x-auto px-4 py-1.5 sm:-mx-1.5 sm:scroll-px-1.5 sm:px-1.5">
        {symbols?.map((symbol) => (
          <li key={symbol.symbol} className="flex shrink-0 grow basis-[13.75rem] snap-start">
            <Tile
              symbol={symbol}
              live={prices[symbol.symbol]}
              selected={symbol.symbol === selected}
              onSelect={onSelect}
            />
          </li>
        ))}

        {isLoading &&
          [0, 1, 2].map((i) => (
            <li key={i} className="flex shrink-0 grow basis-[13.75rem] snap-start">
              <div className="glass flex w-full flex-col gap-3 p-4">
                <Skeleton className="h-4 w-20" />
                <Skeleton className="h-7 w-32" />
                <Skeleton className="h-9 w-full" />
              </div>
            </li>
          ))}

        {error && !symbols && (
          <li className="flex shrink-0 grow basis-[18rem]">
            <p className="glass w-full p-4 text-ink-muted">
              Could not load the monitored symbols. <span className="text-ink-faint">{error.message}</span>
            </p>
          </li>
        )}

        {symbols?.length === 0 && (
          <li className="flex shrink-0 grow basis-[18rem]">
            <p className="glass w-full p-4 text-ink-muted">No symbols are monitored yet. Add one to start collecting trades.</p>
          </li>
        )}

        <li className="flex shrink-0 basis-[8.5rem] snap-start">
          <a
            href={`#${SECTION_IDS.admin}`}
            className="flex w-full flex-col items-center justify-center gap-2 rounded-(--radius-glass) border border-dashed border-glass-border p-4 text-center text-[0.8125rem] text-ink-muted transition-colors duration-200 hover:border-sma/50 hover:bg-glass hover:text-ink"
          >
            <span aria-hidden="true" className="grid size-8 place-items-center rounded-full border border-glass-border text-[1.125rem] leading-none">
              +
            </span>
            Add symbol
          </a>
        </li>
      </ul>
    </section>
  )
})

interface TileProps {
  symbol: SymbolResponse
  live: LivePrice | undefined
  selected: boolean
  onSelect: (symbol: string) => void
}

type Flash = { direction: 'up' | 'down'; id: number } | null

const Tile = memo(function Tile({ symbol, live, selected, onSelect }: TileProps) {
  const { base, quote } = splitSymbol(symbol.symbol)
  const price = live?.price ?? symbol.latestPrice
  const change = symbol.change24hPercent
  const spark = useCandles(symbol.symbol, '1m', SPARK_POINTS)
  const reducedMotion = usePrefersReducedMotion()

  // Flash on every price change (state derived during render, React's pattern for "on change").
  const [previous, setPrevious] = useState(price)
  const [flash, setFlash] = useState<Flash>(null)
  if (price !== previous) {
    setPrevious(price)
    if (price !== null && previous !== null) {
      setFlash({ direction: price > previous ? 'up' : 'down', id: (flash?.id ?? 0) + 1 })
    }
  }

  const { values, spanSeconds } = useMemo(() => {
    const candles = spark.data ?? []
    const closes = candles.map((c) => c.close)
    const first = candles[0]
    const last = candles.at(-1)
    if (!first || !last) return { values: closes, spanSeconds: 0 }
    // The newest trade belongs to the open minute; let it move the end of the line.
    const tradeTime = live ? Date.parse(live.tradeTime) / 1000 : Number.NaN
    const lastTime = toUnixSeconds(last.bucketStart)
    if (live && tradeTime >= lastTime) closes[closes.length - 1] = live.price
    const endTime = tradeTime >= lastTime ? tradeTime : lastTime + 60
    return { values: closes, spanSeconds: endTime - toUnixSeconds(first.bucketStart) }
  }, [spark.data, live])

  const first = values[0]
  const end = values.at(-1)
  const spanChange =
    values.length >= 2 && first !== undefined && end !== undefined && first !== 0 ? ((end - first) / first) * 100 : undefined
  // Say what the line really covers: sparse minutes make it longer than 60 buckets suggest.
  const sparkLabel =
    spanChange === undefined
      ? 'Not enough recent trades for a trend'
      : `Past ${formatDuration(spanSeconds)}, ${formatPercent(spanChange)}`

  return (
    <button
      type="button"
      onClick={() => {
        onSelect(symbol.symbol)
      }}
      aria-pressed={selected}
      className={cn(
        'glass group flex w-full flex-col p-4 text-left transition-[background-color,border-color] duration-200 hover:bg-glass-strong',
        selected && 'tile-selected',
      )}
    >
      <span className="flex items-center justify-between gap-2">
        <span className="text-[0.8125rem] font-semibold tracking-tight">
          {base}
          {quote && <span className="font-normal text-ink-faint">/{quote}</span>}
        </span>
        {change === null ? (
          <span className="text-[0.75rem] text-ink-faint">no 24h data</span>
        ) : (
          <span
            className={cn(
              'num rounded-full px-2 py-0.5 text-[0.75rem] font-medium',
              change > 0 && 'bg-up-soft text-up',
              change < 0 && 'bg-down-soft text-down',
              change === 0 && 'bg-glass-strong text-ink-muted',
            )}
          >
            {formatPercent(change)}
            <span className="sr-only"> in 24 hours</span>
          </span>
        )}
      </span>

      <span className="num mt-2.5 block text-[1.5rem] leading-none font-semibold tracking-[-0.02em]">
        {price === null ? (
          <span className="text-ink-faint">Waiting</span>
        ) : (
          <span key={flash?.id} className={cn('inline-block', flash && !reducedMotion && `price-flash-${flash.direction}`)}>
            {formatPrice(price, symbol.symbol)}
          </span>
        )}
      </span>

      <Sparkline values={values} label={sparkLabel} className="mt-3.5 h-9 w-full" />
    </button>
  )
})
