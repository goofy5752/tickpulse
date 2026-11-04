import { describe, expect, it } from 'vitest'
import type { CandleResponse } from '@/api/types'
import { fillGaps, windowIndexRange } from './chartData'

const MIN = 60
const base = Date.UTC(2026, 10, 3, 12, 0) / 1000

function candle(minute: number, close = 100): CandleResponse {
  return {
    bucketStart: new Date((base + minute * MIN) * 1000).toISOString(),
    open: close,
    high: close,
    low: close,
    close,
    volume: 1,
  }
}

describe('fillGaps', () => {
  it('fillGaps_MissingBuckets_BecomeWhitespaceRows', () => {
    const rows = fillGaps([candle(0), candle(1), candle(4)], MIN)

    expect(rows.map((row) => row.time)).toEqual([0, 1, 2, 3, 4].map((m) => base + m * MIN))
    expect(rows.map((row) => row.candle !== undefined)).toEqual([true, true, false, false, true])
  })

  it('fillGaps_GapLargerThanCap_KeepsRowsCompressed', () => {
    const rows = fillGaps([candle(0), candle(5000)], MIN, 100)

    expect(rows).toHaveLength(2)
  })

  it('fillGaps_Empty_ReturnsEmpty', () => {
    expect(fillGaps([], MIN)).toEqual([])
  })
})

describe('windowIndexRange', () => {
  const times = [0, 1, 2, 3, 4].map((m) => base + m * MIN)

  it('windowIndexRange_InsideData_MapsToInclusiveIndices', () => {
    // End is exclusive: [1, 3) covers minutes 1 and 2.
    expect(windowIndexRange(times, base + MIN, base + 3 * MIN, MIN)).toEqual({
      first: 1,
      last: 2,
      clippedStart: false,
      clippedEnd: false,
    })
  })

  it('windowIndexRange_ReachesPastBothEnds_IsClipped', () => {
    expect(windowIndexRange(times, base - 10 * MIN, base + 60 * MIN, MIN)).toEqual({
      first: 0,
      last: 4,
      clippedStart: true,
      clippedEnd: true,
    })
  })

  it('windowIndexRange_EndsRightAfterLastBucket_IsNotClipped', () => {
    // The usual "up to now" window ends where the in-progress bucket would start.
    expect(windowIndexRange(times, base + 2 * MIN, base + 5 * MIN, MIN)).toEqual({
      first: 2,
      last: 4,
      clippedStart: false,
      clippedEnd: false,
    })
  })

  it('windowIndexRange_OutsideData_IsNull', () => {
    expect(windowIndexRange(times, base + 100 * MIN, base + 120 * MIN, MIN)).toBeNull()
    expect(windowIndexRange([], base, base + MIN, MIN)).toBeNull()
  })
})
