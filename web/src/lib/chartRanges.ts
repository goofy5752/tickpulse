import type { Period } from '@/api/types'

/**
 * Quick ranges, as price apps offer them: how far back to look, with a candle size that suits it.
 * Each sets the period (and with it the SMA panel's p) and how many of the newest buckets fill the view.
 */
export const RANGES = [
  { key: '1H', label: 'One hour of 1-minute candles', p: '1m', bars: 60 },
  { key: '1D', label: 'One day of 5-minute candles', p: '5m', bars: 288 },
  { key: '1W', label: 'One week of 30-minute candles', p: '30m', bars: 336 },
  { key: '1M', label: 'One month of daily candles', p: '1d', bars: 31 },
  { key: '3M', label: 'Three months of daily candles', p: '1d', bars: 92 },
  { key: '6M', label: 'Six months of daily candles', p: '1d', bars: 183 },
] as const satisfies readonly { key: string; label: string; p: Period; bars: number }[]

export type RangeKey = (typeof RANGES)[number]['key']
