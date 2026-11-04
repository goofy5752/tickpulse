import {
  CandlestickSeries,
  ColorType,
  createChart,
  CrosshairMode,
  LineSeries,
  LineStyle,
  type CandlestickData,
  type IChartApi,
  type IPriceLine,
  type IPrimitivePaneRenderer,
  type IPrimitivePaneView,
  type ISeriesApi,
  type ISeriesPrimitive,
  type Logical,
  type MouseEventParams,
  type SeriesAttachedParameter,
  type UTCTimestamp,
} from 'lightweight-charts'
import { useEffect, useMemo, useRef, useState } from 'react'
import type { CandleResponse, IsoDateTime, LivePrice, Period, SmaResponse } from '@/api/types'
import { setSeriesData, type CandleRow } from '@/lib/chartSeries'
import { fillGaps, windowIndexRange, type WindowRange } from '@/lib/chartData'
import { mergeTrade, reconcileBar, type OhlcBar } from '@/lib/liveBar'
import { formatPercent, formatPlainPrice, formatPrice, formatTickmarks, formatUtc, priceDecimals } from '@/lib/format'
import { PERIOD_SECONDS, rollingSma, toUnixSeconds, withPoint } from '@/lib/sma'
import { cn } from '@/lib/utils'

export interface PriceChartProps {
  candles: CandleResponse[]
  period: Period
  /** Draws the rolling SMA with the same n, and shades its window unless `highlight` says otherwise. */
  sma?: SmaResponse
  /** The shaded calculation window; defaults to `sma.windowStart..windowEnd` (end exclusive). */
  highlight?: { from: IsoDateTime; to: IsoDateTime }
  /** The latest trade, merged into the last candle so the chart moves between refetches. */
  live?: LivePrice
  symbol: string
  /** Fits the whole series into view whenever this changes (new symbol or period). */
  fitKey: string
  className?: string
}

// ---------------------------------------------------------------------------------------------
// Colours: read from the theme tokens so the chart follows dark and light mode.

interface Palette {
  up: string
  down: string
  sma: string
  text: string
  grid: string
  crosshair: string
  label: string
}

let probe: CanvasRenderingContext2D | null | undefined

/** Normalises any CSS colour (including `rgb(r g b / a)`) into a form the chart parses. */
function cssColor(name: string, fallback: string): string {
  const value = getComputedStyle(document.documentElement).getPropertyValue(name).trim()
  probe ??= document.createElement('canvas').getContext('2d')
  if (!probe || !value) return fallback
  probe.fillStyle = fallback
  probe.fillStyle = value
  return probe.fillStyle
}

function withAlpha(color: string, alpha: number): string {
  const hex = /^#([0-9a-f]{2})([0-9a-f]{2})([0-9a-f]{2})$/i.exec(color)
  if (hex) {
    const [r, g, b] = hex.slice(1).map((part) => parseInt(part, 16))
    return `rgba(${r ?? 0}, ${g ?? 0}, ${b ?? 0}, ${alpha})`
  }
  const rgba = /^rgba?\(([^,]+),([^,]+),([^,)]+)/.exec(color)
  return rgba ? `rgba(${rgba[1]?.trim()}, ${rgba[2]?.trim()}, ${rgba[3]?.trim()}, ${alpha})` : color
}

function readPalette(): Palette {
  return {
    up: cssColor('--color-up', '#5eead4'),
    down: cssColor('--color-down', '#fb7185'),
    sma: cssColor('--color-sma', '#c084fc'),
    text: cssColor('--color-ink-faint', '#9b9bb0'),
    grid: cssColor('--color-glass-border', '#222230'),
    crosshair: cssColor('--color-ink-muted', '#a0a0b8'),
    label: cssColor('--color-bg', '#07070d'),
  }
}

// ---------------------------------------------------------------------------------------------
// The shaded SMA window, drawn behind the candles as a series primitive.

class WindowShade implements ISeriesPrimitive {
  private chart: SeriesAttachedParameter['chart'] | null = null
  private requestUpdate: (() => void) | null = null
  private range: WindowRange | null = null
  private text = ''
  private palette: Palette | null = null

  private readonly renderer: IPrimitivePaneRenderer = {
    draw: () => undefined,
    drawBackground: (target) => {
      this.draw(target)
    },
  }

  private readonly view: IPrimitivePaneView = {
    zOrder: () => 'bottom',
    renderer: () => this.renderer,
  }

  attached(param: SeriesAttachedParameter) {
    this.chart = param.chart
    this.requestUpdate = param.requestUpdate
  }

  detached() {
    this.chart = null
    this.requestUpdate = null
  }

  paneViews() {
    return [this.view]
  }

  update(range: WindowRange | null, text: string, palette: Palette) {
    this.range = range
    this.text = text
    this.palette = palette
    this.requestUpdate?.()
  }

  private draw(target: Parameters<IPrimitivePaneRenderer['draw']>[0]) {
    const range = this.range
    const palette = this.palette
    const timeScale = this.chart?.timeScale()
    if (!range || !palette || !timeScale) return

    const first = timeScale.logicalToCoordinate(range.first as Logical)
    const last = timeScale.logicalToCoordinate(range.last as Logical)
    const next = timeScale.logicalToCoordinate((range.first + 1) as Logical)
    if (first === null || last === null) return
    const half = next === null ? 3 : Math.abs(next - first) / 2

    target.useBitmapCoordinateSpace(({ context: ctx, bitmapSize, horizontalPixelRatio: hr, verticalPixelRatio: vr }) => {
      const x1 = Math.round((first - half) * hr)
      const x2 = Math.round((last + half) * hr)
      if (x2 <= 0 || x1 >= bitmapSize.width) return

      const gradient = ctx.createLinearGradient(0, 0, 0, bitmapSize.height)
      gradient.addColorStop(0, withAlpha(palette.sma, 0.13))
      gradient.addColorStop(1, withAlpha(palette.sma, 0.04))
      ctx.fillStyle = gradient
      ctx.fillRect(x1, 0, x2 - x1, bitmapSize.height)

      ctx.strokeStyle = withAlpha(palette.sma, 0.55)
      ctx.lineWidth = Math.max(1, Math.floor(hr))
      ctx.setLineDash([3 * hr, 4 * hr])
      ctx.beginPath()
      if (!range.clippedStart) {
        ctx.moveTo(x1 + 0.5, 0)
        ctx.lineTo(x1 + 0.5, bitmapSize.height)
      }
      if (!range.clippedEnd) {
        ctx.moveTo(x2 - 0.5, 0)
        ctx.lineTo(x2 - 0.5, bitmapSize.height)
      }
      ctx.stroke()
      ctx.setLineDash([])

      if (this.text) {
        ctx.font = `500 ${Math.round(11 * vr)}px Inter, ui-sans-serif, system-ui, sans-serif`
        ctx.fillStyle = palette.sma
        ctx.textBaseline = 'top'
        const width = ctx.measureText(this.text).width
        const inset = 8 * hr
        // Inside the window when it fits; otherwise hung off its right edge, into the empty top margin.
        const x = x2 - x1 >= width + inset * 2 ? Math.max(x1, 0) + inset : Math.max(inset, x2 - inset - width)
        ctx.fillText(this.text, x, 9 * vr)
      }
    })
  }
}

// ---------------------------------------------------------------------------------------------

type Bar = CandlestickData<UTCTimestamp>

function toBar(candle: CandleResponse): Bar {
  return {
    time: toUnixSeconds(candle.bucketStart) as UTCTimestamp,
    open: candle.open,
    high: candle.high,
    low: candle.low,
    close: candle.close,
  }
}

function toChartBar(bar: OhlcBar): Bar {
  return { ...bar, time: bar.time as UTCTimestamp }
}

/** The newest bar as the live trades have shaped it since the last fetch. */
interface LiveState {
  /** Symbol and period the bar belongs to; a change starts over from the fetched candle. */
  key: string
  server: Bar | undefined
  trade: LivePrice | undefined
  bar: OhlcBar | undefined
}

interface Hover {
  bar: Bar
  sma: number | undefined
}

export function PriceChart({ candles, period, sma, highlight, live, symbol, fitKey, className }: PriceChartProps) {
  const containerRef = useRef<HTMLDivElement>(null)
  const chartRef = useRef<IChartApi | null>(null)
  const candleSeriesRef = useRef<ISeriesApi<'Candlestick'> | null>(null)
  const smaSeriesRef = useRef<ISeriesApi<'Line'> | null>(null)
  const shadeRef = useRef<WindowShade | null>(null)
  const priceLineRef = useRef<IPriceLine | null>(null)
  const fittedKeyRef = useRef<string | null>(null)
  const [hover, setHover] = useState<Hover | null>(null)
  /** Bumped when the document theme flips, so the data effect repaints with the new palette. */
  const [themeVersion, setThemeVersion] = useState(0)

  const periodSeconds = PERIOD_SECONDS[period]
  const n = sma?.requestedDataPoints ?? 0

  const rows = useMemo(() => fillGaps(candles, periodSeconds), [candles, periodSeconds])
  // The bucket still open at the API's reference time has no final close yet, and the API leaves
  // it out of every window, so the line does too. The point for the window's last bucket
  // (windowEnd − p) is the API's own value, set even when that bucket had no candle, so the line
  // and the figure in the analytics panel always agree there.
  const referenceTime = sma ? toUnixSeconds(sma.referenceTime) : undefined
  const anchorTime = sma ? toUnixSeconds(sma.windowEnd) - periodSeconds : undefined
  const smaValue = sma?.value
  const smaLine = useMemo(() => {
    const closed =
      referenceTime === undefined
        ? candles
        : candles.filter((c) => toUnixSeconds(c.bucketStart) + periodSeconds <= referenceTime)
    const line = rollingSma(closed, n, periodSeconds)
    const first = candles[0]
    if (anchorTime === undefined || smaValue === undefined || !first || anchorTime < toUnixSeconds(first.bucketStart)) {
      return line
    }
    return withPoint(line, { time: anchorTime, value: smaValue })
  }, [candles, n, periodSeconds, referenceTime, anchorTime, smaValue])
  const lastBar = useMemo(() => {
    const last = candles.at(-1)
    return last ? toBar(last) : undefined
  }, [candles])

  // Live trades accumulate into the newest bar (state derived during render): each trade widens
  // its range, a refetch reconciles with the server's candle, a new bucket starts from its trade.
  const liveKey = `${symbol}|${period}`
  const [liveState, setLiveState] = useState<LiveState>(() => ({ key: liveKey, server: lastBar, trade: undefined, bar: lastBar }))
  if (liveState.key !== liveKey || liveState.server !== lastBar || liveState.trade !== live) {
    let bar: OhlcBar | undefined =
      liveState.key !== liveKey ? lastBar : liveState.server !== lastBar ? reconcileBar(liveState.bar, lastBar) : liveState.bar
    if (live && live !== liveState.trade) bar = mergeTrade(bar, live, periodSeconds)
    setLiveState({ key: liveKey, server: lastBar, trade: live, bar })
  }
  const latest = liveState.bar
  /** Mirrors the accumulated bar for effects that repaint the series without a new trade. */
  const latestRef = useRef<{ key: string; bar: OhlcBar | undefined } | null>(null)

  const windowFrom = highlight?.from ?? sma?.windowStart
  const windowTo = highlight?.to ?? sma?.windowEnd
  const range = useMemo(() => {
    if (!windowFrom || !windowTo) return null
    return windowIndexRange(
      rows.map((row) => row.time),
      toUnixSeconds(windowFrom),
      toUnixSeconds(windowTo),
      periodSeconds,
    )
  }, [rows, windowFrom, windowTo, periodSeconds])

  // Create the chart once; resize with the container; follow theme flips; dispose on unmount.
  useEffect(() => {
    const container = containerRef.current
    if (!container) return
    const palette = readPalette()

    const chart = createChart(container, {
      width: container.clientWidth,
      height: container.clientHeight,
      layout: {
        background: { type: ColorType.Solid, color: 'transparent' },
        textColor: palette.text,
        fontFamily: "Inter, ui-sans-serif, system-ui, -apple-system, 'Segoe UI', sans-serif",
        fontSize: 11,
      },
      grid: {
        vertLines: { visible: false },
        horzLines: { color: palette.grid },
      },
      rightPriceScale: { borderVisible: false, scaleMargins: { top: 0.14, bottom: 0.08 } },
      timeScale: { borderVisible: false, rightOffset: 3, barSpacing: 9, minBarSpacing: 1.5 },
      crosshair: {
        mode: CrosshairMode.Normal,
        vertLine: { color: withAlpha(palette.crosshair, 0.5), style: LineStyle.Dashed, labelBackgroundColor: palette.sma },
        horzLine: { color: withAlpha(palette.crosshair, 0.5), style: LineStyle.Dashed, labelBackgroundColor: palette.sma },
      },
      localization: { priceFormatter: formatPlainPrice, tickmarksPriceFormatter: formatTickmarks },
      // Let a vertical swipe scroll the page on touch screens instead of panning the chart.
      handleScroll: { vertTouchDrag: false },
    })

    const candleSeries = chart.addSeries(CandlestickSeries, {
      upColor: palette.up,
      downColor: palette.down,
      wickUpColor: palette.up,
      wickDownColor: palette.down,
      borderVisible: false,
      priceLineColor: withAlpha(palette.crosshair, 0.6),
      priceLineStyle: LineStyle.Dotted,
    })
    const smaSeries = chart.addSeries(LineSeries, {
      color: palette.sma,
      lineWidth: 2,
      priceLineVisible: false,
      lastValueVisible: false,
      crosshairMarkerRadius: 3,
      crosshairMarkerBorderColor: palette.sma,
    })
    const shade = new WindowShade()
    candleSeries.attachPrimitive(shade)

    chartRef.current = chart
    candleSeriesRef.current = candleSeries
    smaSeriesRef.current = smaSeries
    shadeRef.current = shade
    priceLineRef.current = null
    fittedKeyRef.current = null

    const onCrosshair = (param: MouseEventParams) => {
      const bar = param.seriesData.get(candleSeries)
      if (!bar || !('open' in bar)) {
        setHover(null)
        return
      }
      const smaPoint = param.seriesData.get(smaSeries)
      setHover({
        bar: bar as Bar,
        sma: smaPoint && 'value' in smaPoint ? smaPoint.value : undefined,
      })
    }
    chart.subscribeCrosshairMove(onCrosshair)

    const resize = new ResizeObserver(([entry]) => {
      if (!entry) return
      const { width, height } = entry.contentRect
      chart.resize(Math.floor(width), Math.floor(height))
    })
    resize.observe(container)

    const themeWatch = new MutationObserver(() => {
      setThemeVersion((v) => v + 1)
    })
    themeWatch.observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme'] })

    return () => {
      themeWatch.disconnect()
      resize.disconnect()
      chart.unsubscribeCrosshairMove(onCrosshair)
      chart.remove()
      chartRef.current = null
      candleSeriesRef.current = null
      smaSeriesRef.current = null
      shadeRef.current = null
      priceLineRef.current = null
    }
  }, [])

  // Data, colours and the shaded window.
  useEffect(() => {
    const chart = chartRef.current
    const candleSeries = candleSeriesRef.current
    const smaSeries = smaSeriesRef.current
    const shade = shadeRef.current
    if (!chart || !candleSeries || !smaSeries || !shade) return

    const palette = readPalette()
    const precision = priceDecimals(lastBar?.close ?? 1)
    const priceFormat = { type: 'price' as const, precision, minMove: 10 ** -precision }
    const intraday = period !== '1d' && period !== '1w'

    chart.applyOptions({
      layout: { textColor: palette.text },
      grid: { horzLines: { color: palette.grid } },
      timeScale: { timeVisible: intraday, secondsVisible: false },
      crosshair: {
        vertLine: { color: withAlpha(palette.crosshair, 0.5), labelBackgroundColor: palette.sma },
        horzLine: { color: withAlpha(palette.crosshair, 0.5), labelBackgroundColor: palette.sma },
      },
    })
    candleSeries.applyOptions({
      upColor: palette.up,
      downColor: palette.down,
      wickUpColor: palette.up,
      wickDownColor: palette.down,
      priceLineColor: withAlpha(palette.crosshair, 0.6),
      priceFormat,
    })
    smaSeries.applyOptions({ color: palette.sma, crosshairMarkerBorderColor: palette.sma, priceFormat })

    // Never leaves the SMA series empty; see setSeriesData for the charting-library bug it avoids.
    setSeriesData(
      candleSeries,
      smaSeries,
      rows.map((row): CandleRow => (row.candle ? toBar(row.candle) : { time: row.time as UTCTimestamp })),
      smaLine,
    )

    if (priceLineRef.current) smaSeries.removePriceLine(priceLineRef.current)
    priceLineRef.current = sma
      ? smaSeries.createPriceLine({
          price: sma.value,
          color: palette.sma,
          lineVisible: false,
          axisLabelVisible: true,
          axisLabelColor: palette.sma,
          axisLabelTextColor: palette.label,
          title: `SMA ${sma.requestedDataPoints}`,
        })
      : null

    shade.update(range, sma ? `SMA window, n = ${sma.requestedDataPoints}` : '', palette)

    // setData dropped the live-accumulated newest bar; put it back if it belongs to this series.
    const accumulated = latestRef.current
    if (accumulated?.key === liveKey && accumulated.bar && lastBar && accumulated.bar.time >= lastBar.time) {
      safeUpdate(candleSeries, accumulated.bar)
    }

    if (fittedKeyRef.current !== fitKey && rows.length > 0) {
      chart.timeScale().fitContent()
      fittedKeyRef.current = fitKey
    }
  }, [rows, smaLine, sma, range, period, lastBar, fitKey, themeVersion, liveKey])

  // Live trades move the newest candle between refetches.
  useEffect(() => {
    latestRef.current = { key: liveKey, bar: latest }
    const series = candleSeriesRef.current
    if (series && latest && latest !== lastBar) safeUpdate(series, latest)
  }, [latest, lastBar, liveKey])

  const shown = hover?.bar ?? latest
  const shownSma = hover ? hover.sma : smaLine.at(-1)?.value
  const change = shown && shown.open !== 0 ? ((shown.close - shown.open) / shown.open) * 100 : undefined
  const first = candles[0]
  const lastCandle = candles.at(-1)

  return (
    <figure className={cn('flex min-w-0 flex-col', className)}>
      <div className="flex min-h-5 items-start gap-2">
        <div
          aria-hidden="true"
          className="num flex min-w-0 flex-1 flex-wrap items-baseline gap-x-3.5 gap-y-1 text-[0.75rem] text-ink-muted"
        >
          {shown && (
            <>
              <span className="text-ink-faint">
                {formatUtc(new Date(shown.time * 1000).toISOString(), period)}
              </span>
              <Ohlc label="O" value={shown.open} />
              <Ohlc label="H" value={shown.high} />
              <Ohlc label="L" value={shown.low} />
              <Ohlc label="C" value={shown.close} />
              {change !== undefined && (
                <span className={cn(change > 0 && 'text-up', change < 0 && 'text-down')}>{formatPercent(change)}</span>
              )}
              {shownSma !== undefined && sma && (
                <span>
                  <span className="text-sma">SMA {sma.requestedDataPoints}</span>{' '}
                  <span className="text-ink">{formatPrice(shownSma)}</span>
                </span>
              )}
            </>
          )}
        </div>
        
      </div>
      <div ref={containerRef} className="relative mt-3 min-h-0 w-full flex-1 overflow-hidden" />
      <figcaption className="sr-only">
        {first && lastCandle
          ? `${symbol} ${period} candles from ${formatUtc(first.bucketStart, period)} to ${formatUtc(lastCandle.bucketStart, period)} UTC, ${candles.length} buckets. Last close ${formatPrice(lastCandle.close, symbol)}.` +
            (sma
              ? ` SMA ${sma.requestedDataPoints} is ${formatPrice(sma.value, symbol)}, computed from ${formatUtc(sma.windowStart, period)} to ${formatUtc(sma.windowEnd, period)}.`
              : '')
          : `No ${period} candles for ${symbol} yet.`}
      </figcaption>
    </figure>
  )
}

/** `update` throws for a bar older than the series' newest one; a stale trade must not crash the page. */
function safeUpdate(series: ISeriesApi<'Candlestick'>, bar: OhlcBar) {
  try {
    series.update(toChartBar(bar))
  } catch {
    // The next refetch redraws the series from the API anyway.
  }
}

function Ohlc({ label, value }: { label: string; value: number }) {
  return (
    <span>
      <span className="text-ink-faint">{label}</span> <span className="text-ink">{formatPrice(value)}</span>
    </span>
  )
}
