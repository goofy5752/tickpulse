import { isValidSmaN, SMA_MAX_POINTS } from '@/api/queries'
import type { Period } from '@/api/types'

/** The SMA query the dashboard shows; the chart and the analytics panel share it. */
export interface SmaParams {
  /** Number of buckets; NaN while the input holds something that is not a valid n. */
  n: number
  p: Period
  /** `datetime-local` value read as UTC (e.g. "2026-10-14T00:00"), or "" for "up to now". */
  s: string
}

export const DEFAULT_SMA_PARAMS: SmaParams = { n: 20, p: '1d', s: '' }

export const N_HINT = `Enter a whole number from 1 to ${SMA_MAX_POINTS}.`

/** Parses the n field strictly: digits only, in range, otherwise NaN. */
export function parseN(text: string): number {
  const trimmed = text.trim()
  if (!/^\d+$/.test(trimmed)) return Number.NaN
  const n = Number(trimmed)
  return isValidSmaN(n) ? n : Number.NaN
}

/** The `s` query value: the picked minute as an ISO-8601 UTC instant, or undefined for "now". */
export function startToIso(s: string): string | undefined {
  if (!/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}$/.test(s)) return undefined
  return `${s}:00Z`
}
