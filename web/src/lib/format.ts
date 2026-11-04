/*
 * Display formatting for the dashboard.
 *
 * Every figure uses en-US conventions on purpose (comma thousands separators, dot decimals, UTC
 * times): the API speaks invariant-culture numbers and ISO-8601 UTC, the dashboard copy is English,
 * and traders read pair prices this way. Following the browser locale would make the same price
 * look different from the API response shown beside it.
 */
import type { IsoDateTime, Period } from '@/api/types'

const MINUS = '−'

const numberFormats = new Map<string, Intl.NumberFormat>()

function numberFormat(minimumFractionDigits: number, maximumFractionDigits = minimumFractionDigits) {
  const key = `${minimumFractionDigits}:${maximumFractionDigits}`
  let format = numberFormats.get(key)
  if (!format) {
    format = new Intl.NumberFormat('en-US', { minimumFractionDigits, maximumFractionDigits })
    numberFormats.set(key, format)
  }
  return format
}

/** Significant digits kept for prices below 0.0001, so 0.000001234 does not round to 0.00000123. */
const TINY_PRICE_SIGNIFICANT_DIGITS = 4

/**
 * Decimals that keep a price readable at its magnitude: 97,412.30, 3.1415, 0.7214, 0.000123 and,
 * below 0.0001, four significant digits (0.000001234).
 */
export function priceDecimals(value: number): number {
  const abs = Math.abs(value)
  if (abs >= 10 || abs === 0 || !Number.isFinite(abs)) return 2
  if (abs >= 0.01) return 4
  if (abs >= 0.0001) return 6
  return Math.min(20, TINY_PRICE_SIGNIFICANT_DIGITS - 1 - Math.floor(Math.log10(abs)))
}

const USD_QUOTES = ['USDT', 'USDC', 'FDUSD', 'TUSD', 'USD'] as const

/** Splits a pair into base and quote for display, e.g. BTCUSDT → BTC / USDT. */
export function splitSymbol(symbol: string): { base: string; quote: string } {
  for (const quote of [...USD_QUOTES, 'BTC', 'ETH', 'BNB', 'EUR']) {
    if (symbol.length > quote.length && symbol.endsWith(quote)) {
      return { base: symbol.slice(0, -quote.length), quote }
    }
  }
  return { base: symbol, quote: '' }
}

/** A price with a dollar sign for USD-pegged quotes and none otherwise. */
export function formatPrice(value: number, symbol?: string): string {
  const text = numberFormat(priceDecimals(value)).format(value)
  const quote = symbol ? splitSymbol(symbol).quote : ''
  return (USD_QUOTES as readonly string[]).includes(quote) ? `$${text}` : text
}

export function formatPercent(value: number): string {
  const text = numberFormat(2).format(Math.abs(value))
  if (value > 0) return `+${text}%`
  if (value < 0) return `${MINUS}${text}%`
  return `${text}%`
}

export function formatCount(value: number): string {
  return numberFormat(0).format(value)
}

/** Compact counts for big totals: 8.4M, 312K. */
export function formatCompact(value: number): string {
  return new Intl.NumberFormat('en-US', { notation: 'compact', maximumFractionDigits: 1 }).format(value)
}

/** 35s, 42m, 23h 58m, 2d 4h. */
export function formatDuration(totalSeconds: number): string {
  const s = Math.max(0, Math.floor(totalSeconds))
  const days = Math.floor(s / 86_400)
  const hours = Math.floor((s % 86_400) / 3_600)
  const minutes = Math.floor((s % 3_600) / 60)
  if (days > 0) return `${days}d ${hours}h`
  if (hours > 0) return `${hours}h ${String(minutes).padStart(2, '0')}m`
  if (minutes > 0) return `${minutes}m`
  return `${s}s`
}

export function formatLag(ms: number): string {
  if (ms < 1000) return `${Math.max(0, Math.round(ms))} ms`
  return `${(ms / 1000).toFixed(1)} s`
}

const dateFormat = new Intl.DateTimeFormat('en-US', { timeZone: 'UTC', month: 'short', day: 'numeric' })
const dateYearFormat = new Intl.DateTimeFormat('en-US', {
  timeZone: 'UTC',
  month: 'short',
  day: 'numeric',
  year: 'numeric',
})
const timeFormat = new Intl.DateTimeFormat('en-US', {
  timeZone: 'UTC',
  hour: '2-digit',
  minute: '2-digit',
  hourCycle: 'h23',
})

/** A UTC instant at the precision a period needs: "Oct 14" for days and weeks, "Oct 14, 09:30" below. */
export function formatUtc(iso: IsoDateTime, period: Period, now: Date = new Date()): string {
  const date = new Date(iso)
  const day = date.getUTCFullYear() === now.getUTCFullYear() ? dateFormat.format(date) : dateYearFormat.format(date)
  return period === '1d' || period === '1w' ? day : `${day}, ${timeFormat.format(date)}`
}

function decimalsNeeded(value: number): number {
  for (let d = 0; d < 8; d++) {
    const f = 10 ** d
    if (Math.abs(Math.round(value * f) / f - value) < 1e-9 * Math.max(1, Math.abs(value))) return d
  }
  return 8
}

/** Price-axis labels: grouped, with only as many decimals as the ticks need (108,000 not 108000.00). */
export function formatTickmarks(prices: readonly number[]): string[] {
  const decimals = prices.reduce((max, p) => Math.max(max, decimalsNeeded(p)), 0)
  const format = numberFormat(decimals)
  return prices.map((p) => format.format(p))
}

/** A price without a currency sign, at the precision its magnitude needs. */
export function formatPlainPrice(value: number): string {
  return numberFormat(priceDecimals(value)).format(value)
}
