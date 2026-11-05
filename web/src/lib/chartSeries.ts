import type { CandlestickData, HistogramData, ISeriesApi, LineData, UTCTimestamp, WhitespaceData } from 'lightweight-charts'
import type { TimeValue } from '@/lib/sma'

export type CandleRow = CandlestickData<UTCTimestamp> | WhitespaceData<UTCTimestamp>
export type CloseRow = LineData<UTCTimestamp> | WhitespaceData<UTCTimestamp>
export type VolumeRow = HistogramData<UTCTimestamp> | WhitespaceData<UTCTimestamp>

/** A candle as a point of the line view: its close; a gap stays a gap. */
export function toCloseRow(row: CandleRow): CloseRow {
  return 'close' in row ? { time: row.time, value: row.close } : { time: row.time }
}

/**
 * Hands the candles and the SMA line to their series.
 *
 * The SMA series is never left empty while there are candles: without a line it gets one
 * whitespace point (no value, nothing drawn) at the first candle's time. lightweight-charts 5.2.1
 * has a shortcut for a chart whose only series with data is re-set: it rebuilds its time points
 * as new objects, but when the times are unchanged it keeps its old sorted list. The next
 * setData on the other series then cleans up through that stale list and deletes live points,
 * so candles lose their place on the time scale and every paint throws "Value is null". Clearing
 * the SMA (a symbol switch while the next one loads) followed by candles on the same time grid
 * (gaps drawn as whitespace) was exactly that path; a series that always holds a point never is.
 */
export function setSeriesData(
  candleSeries: ISeriesApi<'Candlestick'>,
  smaSeries: ISeriesApi<'Line'>,
  candles: CandleRow[],
  line: readonly TimeValue[],
  closeSeries?: ISeriesApi<'Area'>,
  volume?: { series: ISeriesApi<'Histogram'>; rows: VolumeRow[] },
): void {
  candleSeries.setData(candles)
  // Volume bars under the price, on the same grid: a gap is whitespace here too.
  volume?.series.setData(volume.rows)
  // The line view: the same time grid, so all series always share every time point.
  closeSeries?.setData(candles.map(toCloseRow))
  const first = candles[0]
  if (line.length === 0 && first) {
    smaSeries.setData([{ time: first.time }])
    return
  }
  smaSeries.setData(line.map((p): LineData<UTCTimestamp> => ({ time: p.time as UTCTimestamp, value: p.value })))
}
