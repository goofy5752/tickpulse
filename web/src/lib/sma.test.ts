import { describe, expect, it } from 'vitest'
import { PERIOD_SECONDS, rollingSma, toUnixSeconds, withPoint } from './sma'

const MIN = 60

function closes(...values: [minute: number, close: number][]) {
  return values.map(([minute, close]) => ({
    bucketStart: new Date(Date.UTC(2026, 10, 3, 12, minute)).toISOString(),
    close,
  }))
}

const t = (minute: number) => Date.UTC(2026, 10, 3, 12, minute) / 1000

describe('rollingSma', () => {
  it('rollingSma_Contiguous_AveragesLastNCloses', () => {
    const line = rollingSma(closes([0, 10], [1, 20], [2, 30], [3, 40]), 3, MIN)

    expect(line).toEqual([
      { time: t(2), value: 20 },
      { time: t(3), value: 30 },
    ])
  })

  it('rollingSma_NIsOne_EqualsCloses', () => {
    const line = rollingSma(closes([0, 1.5], [1, 2.5]), 1, MIN)

    expect(line).toEqual([
      { time: t(0), value: 1.5 },
      { time: t(1), value: 2.5 },
    ])
  })

  it('rollingSma_Gap_AveragesOnlyTheClosesInsideTheTimeWindow', () => {
    // Minute 2 has no trades. The window ending at minute 3 spans minutes 1..3, like the API's.
    const line = rollingSma(closes([0, 10], [1, 20], [3, 40], [4, 50]), 3, MIN)

    expect(line).toEqual([
      { time: t(3), value: 30 },
      { time: t(4), value: 45 },
    ])
  })

  it('rollingSma_WindowNotCoveredByLoadedData_EmitsNothingForIt', () => {
    // A window that would reach before the first loaded candle is skipped, not under-filled.
    expect(rollingSma(closes([0, 10], [1, 20]), 3, MIN)).toEqual([])
  })

  it('rollingSma_UnsortedInput_IsOrderedByTime', () => {
    const line = rollingSma(closes([2, 30], [0, 10], [1, 20]), 2, MIN)

    expect(line.map((point) => point.time)).toEqual([t(1), t(2)])
    expect(line.map((point) => point.value)).toEqual([15, 25])
  })

  it.each([0, -1, 1.5, Number.NaN])('rollingSma_InvalidN_%s_ReturnsEmpty', (n) => {
    expect(rollingSma(closes([0, 10], [1, 20]), n, MIN)).toEqual([])
  })

  it('rollingSma_DailyPeriod_UsesBucketLength', () => {
    const days = [0, 1, 2].map((day) => ({
      bucketStart: new Date(Date.UTC(2026, 9, 1 + day)).toISOString(),
      close: (day + 1) * 100,
    }))

    const line = rollingSma(days, 2, PERIOD_SECONDS['1d'])

    expect(line.map((point) => point.value)).toEqual([150, 250])
  })
})

describe('withPoint', () => {
  it('withPoint_ExistingTime_ReplacesValue', () => {
    expect(withPoint([{ time: 1, value: 1 }, { time: 2, value: 2 }], { time: 2, value: 9 })).toEqual([
      { time: 1, value: 1 },
      { time: 2, value: 9 },
    ])
  })

  it('withPoint_MissingTime_InsertsInOrder', () => {
    // The anchor bucket had no candle, so the rolling line has no point there yet.
    expect(withPoint([{ time: 1, value: 1 }, { time: 3, value: 3 }], { time: 2, value: 9 })).toEqual([
      { time: 1, value: 1 },
      { time: 2, value: 9 },
      { time: 3, value: 3 },
    ])
    expect(withPoint([], { time: 5, value: 5 })).toEqual([{ time: 5, value: 5 }])
    expect(withPoint([{ time: 1, value: 1 }], { time: 7, value: 7 }).at(-1)).toEqual({ time: 7, value: 7 })
  })
})

describe('toUnixSeconds', () => {
  it('toUnixSeconds_ParsesIsoUtc', () => {
    expect(toUnixSeconds('2026-11-03T00:00:00Z')).toBe(Date.UTC(2026, 10, 3) / 1000)
  })
})
