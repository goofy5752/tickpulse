import type { CandleResponse } from '@/api/types'
import { toUnixSeconds } from './sma'

export interface ChartRow {
  /** Bucket start, UNIX seconds (UTC). */
  time: number
  /** Absent for a bucket without trades; the chart draws it as empty space. */
  candle?: CandleResponse
}

/** Above this many empty buckets the gaps stay compressed instead of stretching the time axis. */
const DEFAULT_MAX_FILL = 1000

/**
 * Turns the API's candles (oldest first, buckets without trades missing) into one row per bucket,
 * so gaps show as gaps and every bucket has the same width on the time axis. When the gaps add up
 * to more than `maxFill` empty buckets (e.g. a long outage on a 1m chart) the rows are returned
 * as they are.
 */
export function fillGaps(
  candles: readonly CandleResponse[],
  periodSeconds: number,
  maxFill: number = DEFAULT_MAX_FILL,
): ChartRow[] {
  const rows = candles
    .map((candle) => ({ time: toUnixSeconds(candle.bucketStart), candle }))
    .sort((a, b) => a.time - b.time)

  let missing = 0
  for (let i = 1; i < rows.length; i++) {
    const gap = Math.round(((rows[i]?.time ?? 0) - (rows[i - 1]?.time ?? 0)) / periodSeconds) - 1
    if (gap > 0) missing += gap
  }
  if (missing === 0 || missing > maxFill) return rows

  const filled: ChartRow[] = []
  for (const row of rows) {
    const previous = filled.at(-1)
    if (previous) {
      for (let time = previous.time + periodSeconds; time < row.time; time += periodSeconds) {
        filled.push({ time })
      }
    }
    filled.push(row)
  }
  return filled
}

export interface WindowRange {
  /** First row index inside the window. */
  first: number
  /** Last row index inside the window (inclusive). */
  last: number
  /** The window starts before the first loaded bucket. */
  clippedStart: boolean
  /** The window ends after the bucket that follows the last loaded one. */
  clippedEnd: boolean
}

/**
 * Maps a `[from, toExclusive)` window (UNIX seconds) onto row indices of `times` (ascending).
 * Returns null when no loaded bucket falls inside the window.
 */
export function windowIndexRange(
  times: readonly number[],
  from: number,
  toExclusive: number,
  periodSeconds: number,
): WindowRange | null {
  const first = times.findIndex((time) => time >= from)
  if (first === -1) return null
  let last = -1
  for (let i = times.length - 1; i >= first; i--) {
    if ((times[i] ?? Infinity) < toExclusive) {
      last = i
      break
    }
  }
  if (last < first) return null

  const firstTime = times[0] ?? from
  const lastTime = times.at(-1) ?? toExclusive
  return {
    first,
    last,
    clippedStart: from < firstTime,
    clippedEnd: toExclusive > lastTime + periodSeconds,
  }
}
