import { describe, expect, it } from 'vitest'
import {
  formatDuration,
  formatLag,
  formatPercent,
  formatPrice,
  formatTickmarks,
  formatUtc,
  priceDecimals,
  splitSymbol,
} from './format'

describe('format', () => {
  it('formatPrice_ScalesDecimalsWithMagnitude', () => {
    expect(formatPrice(97412.3, 'BTCUSDT')).toBe('$97,412.30')
    expect(formatPrice(0.7214, 'ADAUSDT')).toBe('$0.7214')
    expect(formatPrice(0.00001234, 'SHIBBTC')).toBe('0.00001234')
  })

  it('formatPrice_BelowOneTenThousandth_KeepsFourSignificantDigits', () => {
    expect(formatPrice(0.000001234, 'PEPEUSDT')).toBe('$0.000001234')
    expect(formatPrice(0.00000005678)).toBe('0.00000005678')
    expect(priceDecimals(0.000001234)).toBe(9)
    expect(formatPrice(0)).toBe('0.00')
  })

  it('formatPercent_UsesTrueMinusAndExplicitPlus', () => {
    expect(formatPercent(1.84)).toBe('+1.84%')
    expect(formatPercent(-0.42)).toBe('−0.42%')
    expect(formatPercent(0)).toBe('0.00%')
  })

  it('formatDuration_PicksTheTwoLargestUnits', () => {
    expect(formatDuration(86_280)).toBe('23h 58m')
    expect(formatDuration(187_200)).toBe('2d 4h')
    expect(formatDuration(42 * 60)).toBe('42m')
    expect(formatDuration(35)).toBe('35s')
  })

  it('formatLag_SwitchesToSecondsAtOneSecond', () => {
    expect(formatLag(38.4)).toBe('38 ms')
    expect(formatLag(1250)).toBe('1.3 s')
  })

  it('formatTickmarks_UsesOnlyTheDecimalsTheTicksNeed', () => {
    expect(formatTickmarks([100000, 104000, 108000])).toEqual(['100,000', '104,000', '108,000'])
    expect(formatTickmarks([0.7, 0.725, 0.75])).toEqual(['0.700', '0.725', '0.750'])
  })

  it('formatUtc_ShowsTimeOnlyBelowDailyPeriods', () => {
    const now = new Date('2026-11-04T12:00:00Z')
    expect(formatUtc('2026-10-14T00:00:00Z', '1d', now)).toBe('Oct 14')
    expect(formatUtc('2026-11-03T22:25:00Z', '5m', now)).toBe('Nov 3, 22:25')
    expect(formatUtc('2025-12-29T00:00:00Z', '1w', now)).toBe('Dec 29, 2025')
  })

  it('splitSymbol_SeparatesKnownQuotes', () => {
    expect(splitSymbol('BTCUSDT')).toEqual({ base: 'BTC', quote: 'USDT' })
    expect(splitSymbol('ETHBTC')).toEqual({ base: 'ETH', quote: 'BTC' })
    expect(splitSymbol('XYZ12')).toEqual({ base: 'XYZ12', quote: '' })
  })
})
