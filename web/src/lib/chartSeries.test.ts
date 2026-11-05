import { AreaSeries, CandlestickSeries, createChart, HistogramSeries, LineSeries, type IChartApi, type UTCTimestamp } from 'lightweight-charts'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import type { TimeValue } from '@/lib/sma'
import { setSeriesData, type CandleRow } from './chartSeries'

// The real charting library: its data layer runs in jsdom, only painting needs a canvas, so each
// chart is removed before the next animation frame could paint it.

const WEEK = 604_800
const T0 = 1_777_852_800 // a Monday, 00:00 UTC
const t = (week: number) => (T0 + week * WEEK) as UTCTimestamp
const WEEKS = 27

function bar(week: number, price: number) {
  return { time: t(week), open: price, high: price + 5, low: price - 5, close: price + 1 }
}

/** A pair with every week traded (BTC after its backfill). */
const full = (): CandleRow[] => Array.from({ length: WEEKS }, (_, w) => bar(w, 100 + w))
/** The same 27-week grid with 13 empty weeks drawn as gaps (ADA while its backfill runs). */
const gappy = (): CandleRow[] => Array.from({ length: WEEKS }, (_, w) => (w < 13 || w === 26 ? bar(w, 1) : { time: t(w) }))
const line = (weeks: number[]): TimeValue[] => weeks.map((w) => ({ time: t(w), value: 100 + w }))

let chart: IChartApi
let host: HTMLElement

beforeEach(() => {
  host = document.createElement('div')
  document.body.appendChild(host)
  Object.defineProperty(window, 'matchMedia', {
    configurable: true,
    value: (query: string) => ({ matches: false, media: query, addEventListener: () => undefined, removeEventListener: () => undefined, addListener: () => undefined, removeListener: () => undefined }),
  })
  chart = createChart(host, { width: 600, height: 300 })
})

afterEach(() => {
  chart.remove()
  host.remove()
})

describe('setSeriesData', () => {
  it('SetSeriesData_FastSymbolSwitch_KeepsEveryCandleOnTheTimeScale', () => {
    const candles = chart.addSeries(CandlestickSeries)
    const sma = chart.addSeries(LineSeries)

    // BTC with its SMA; the same BTC redrawn without the SMA while the next symbol loads; ADA on
    // the same week grid with gaps and a one-point SMA; BTC again; then a live trade.
    setSeriesData(candles, sma, full(), line([19, 20, 21, 22, 23, 24, 25]))
    setSeriesData(candles, sma, full(), [])
    setSeriesData(candles, sma, gappy(), line([25]))
    setSeriesData(candles, sma, full(), line([19, 20, 21, 22, 23, 24, 25]))
    candles.update(bar(26, 200))

    // Every candle is where the time scale says it is; a broken scale returns null here, and
    // painting such a series throws "Value is null".
    for (let week = 0; week < WEEKS; week++) {
      expect(candles.dataByIndex(week), `week ${week}`).toMatchObject({ time: t(week) })
    }
    expect(sma.dataByIndex(19)).toMatchObject({ time: t(19), value: 119 })
  })

  it('SetSeriesData_LineView_HoldsEveryCloseOnTheSameTimeGrid', () => {
    const candles = chart.addSeries(CandlestickSeries)
    const sma = chart.addSeries(LineSeries)
    const closes = chart.addSeries(AreaSeries, { visible: false })

    // The same fast switches as above, with the line view's series alongside.
    setSeriesData(candles, sma, full(), line([19, 20, 21]), closes)
    setSeriesData(candles, sma, full(), [], closes)
    setSeriesData(candles, sma, gappy(), line([25]), closes)
    setSeriesData(candles, sma, full(), line([19, 20, 21]), closes)

    for (let week = 0; week < WEEKS; week++) {
      expect(candles.dataByIndex(week), `candle week ${week}`).toMatchObject({ time: t(week) })
      expect(closes.dataByIndex(week), `close week ${week}`).toMatchObject({ time: t(week), value: 101 + week })
    }
  })

  it('SetSeriesData_LineView_KeepsGapsAsGaps', () => {
    const candles = chart.addSeries(CandlestickSeries)
    const sma = chart.addSeries(LineSeries)
    const closes = chart.addSeries(AreaSeries)

    setSeriesData(candles, sma, gappy(), [], closes)

    // Only traded weeks carry a close; an empty week draws no point and no line through it.
    expect(closes.data().filter((point) => 'value' in point)).toHaveLength(14)
    expect(closes.dataByIndex(13)).toBeNull()
    expect(closes.dataByIndex(26)).toMatchObject({ time: t(26), value: 2 })
  })

  it('SetSeriesData_Volume_FollowsTheCandles_GapsIncluded', () => {
    const candles = chart.addSeries(CandlestickSeries)
    const sma = chart.addSeries(LineSeries)
    const closes = chart.addSeries(AreaSeries)
    const volume = chart.addSeries(HistogramSeries, { priceScaleId: 'volume' })
    const volumeRows = (rows: CandleRow[]) => rows.map((r) => ('close' in r ? { time: r.time, value: r.close * 10 } : { time: r.time }))

    // The same fast switches, with volume bars alongside.
    setSeriesData(candles, sma, full(), line([19, 20, 21]), closes, { series: volume, rows: volumeRows(full()) })
    setSeriesData(candles, sma, gappy(), [], closes, { series: volume, rows: volumeRows(gappy()) })
    setSeriesData(candles, sma, full(), line([19, 20, 21]), closes, { series: volume, rows: volumeRows(full()) })

    for (let week = 0; week < WEEKS; week++) {
      expect(volume.dataByIndex(week), 'volume week ' + String(week)).toMatchObject({ time: t(week), value: (101 + week) * 10 })
      expect(candles.dataByIndex(week), 'candle week ' + String(week)).toMatchObject({ time: t(week) })
    }
  })

  it('SetSeriesData_NoLine_LeavesTheSmaSeriesWithoutPoints', () => {
    const candles = chart.addSeries(CandlestickSeries)
    const sma = chart.addSeries(LineSeries)

    setSeriesData(candles, sma, full(), [])

    expect(sma.data().filter((point) => 'value' in point)).toEqual([])
    expect(candles.data()).toHaveLength(WEEKS)
  })
})
