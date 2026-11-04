import type { IsoDateTime, Period } from '@/api/types'

/** Bucket length per period. Weeks are a fixed 7 days (Monday-aligned on the server). */
export const PERIOD_SECONDS: Record<Period, number> = {
  '1m': 60,
  '5m': 5 * 60,
  '30m': 30 * 60,
  '1d': 24 * 60 * 60,
  '1w': 7 * 24 * 60 * 60,
}

export function toUnixSeconds(iso: IsoDateTime): number {
  return Math.floor(Date.parse(iso) / 1000)
}

export interface TimeValue {
  /** Bucket start, UNIX seconds (UTC). */
  time: number
  value: number
}

/**
 * The rolling SMA line for the chart, computed from candle closes with the same `n` the API uses.
 *
 * Each point averages the closes of the `n` buckets ending at that candle, measured in time and
 * not in rows, the same way the API builds its window: buckets without trades are simply absent
 * and do not pull in older closes. A point is only emitted once its whole window lies inside the
 * loaded candles, so the line never starts with a half-empty average.
 */
export function rollingSma(
  candles: readonly { bucketStart: IsoDateTime; close: number }[],
  n: number,
  periodSeconds: number,
): TimeValue[] {
  if (!Number.isInteger(n) || n < 1 || candles.length === 0) return []

  const points = candles.map((c) => ({ time: toUnixSeconds(c.bucketStart), close: c.close }))
  if (points.some((p, i) => i > 0 && p.time < (points[i - 1]?.time ?? p.time))) {
    points.sort((a, b) => a.time - b.time)
  }

  const firstTime = points[0]?.time ?? 0
  const span = (n - 1) * periodSeconds
  const line: TimeValue[] = []
  let lo = 0

  for (let i = 0; i < points.length; i++) {
    const point = points[i]
    if (!point) continue
    const windowStart = point.time - span
    if (windowStart < firstTime) continue
    while ((points[lo]?.time ?? Infinity) < windowStart) lo++

    let sum = 0
    for (let j = lo; j <= i; j++) sum += points[j]?.close ?? 0
    line.push({ time: point.time, value: sum / (i - lo + 1) })
  }
  return line
}
