import { QueryClient, useQuery } from '@tanstack/react-query'
import { ApiError, apiPaths, getJson } from './client'
import type {
  AveragePriceResponse,
  CandleResponse,
  Period,
  SmaResponse,
  SymbolResponse,
  SystemStatsResponse,
} from './types'

export const SMA_MAX_POINTS = 1000
export const CANDLES_DEFAULT_LIMIT = 300

/** The API accepts `n` in 1..1000; anything else would only produce a 400. */
export function isValidSmaN(n: number): boolean {
  return Number.isInteger(n) && n >= 1 && n <= SMA_MAX_POINTS
}

// No query function reads TanStack's `signal`, on purpose. A query that reads it has its fetch
// aborted whenever it loses its last observer: on every keystroke in the n field, every quick
// period or symbol switch, every unmount. Browser extensions commonly wrap `window.fetch` and
// watch the promise without a rejection handler, so each abort then shows up as an uncaught
// `AbortError` in the console, even though this code handles it. Superseded reads are small; they
// finish and land in the cache, ready if the user comes back to the same parameters.

export const queryKeys = {
  symbols: () => ['symbols'] as const,
  average: (symbol: string) => ['average', symbol] as const,
  sma: (symbol: string, n: number, p: Period, s: string | undefined) => ['sma', symbol, n, p, s ?? null] as const,
  candles: (symbol: string, p: Period, limit: number) => ['candles', symbol, p, limit] as const,
  stats: () => ['stats'] as const,
}

/** 4xx answers (unknown symbol, no data, validation) will not change on retry; 5xx and network errors may. */
function shouldRetry(failureCount: number, error: unknown): boolean {
  if (error instanceof ApiError && error.status >= 400 && error.status < 500) return false
  return failureCount < 3
}

export function createQueryClient(): QueryClient {
  return new QueryClient({
    defaultOptions: {
      queries: {
        retry: shouldRetry,
        staleTime: 5_000,
        refetchOnWindowFocus: false,
      },
    },
  })
}

/**
 * Keeps showing the previous result while a new one loads, but only for the same symbol
 * (index 1 of every symbol-scoped query key). Switching symbols shows a loading state instead
 * of another symbol's numbers.
 */
function keepPreviousForSymbol(symbol: string) {
  return <T>(previous: T | undefined, previousQuery?: { queryKey: readonly unknown[] }): T | undefined =>
    previousQuery?.queryKey[1] === symbol ? previous : undefined
}

function normalise(symbol: string | undefined): string {
  return (symbol ?? '').trim().toUpperCase()
}

export const SYMBOLS_REFRESH_MS = 30_000
/** How often the admin view re-reads the list while a backfill is queued or running. */
export const BACKFILL_POLL_MS = 2_000

/** Fast polling only while someone watches a backfill that is still in flight. */
export function symbolsRefetchInterval(data: readonly SymbolResponse[] | undefined, pollBackfill: boolean): number {
  const inFlight = data?.some((s) => s.isActive && (s.backfillStatus === 'Pending' || s.backfillStatus === 'Running'))
  return pollBackfill && inFlight ? BACKFILL_POLL_MS : SYMBOLS_REFRESH_MS
}

/**
 * All monitored symbols with their latest price, 24h change and backfill status. Every caller
 * shares one cache entry, so an admin change shows up in the ticker tiles too.
 */
export function useSymbols(options: { pollBackfill?: boolean } = {}) {
  const pollBackfill = options.pollBackfill ?? false
  return useQuery({
    queryKey: queryKeys.symbols(),
    queryFn: () => getJson<SymbolResponse[]>(apiPaths.symbols()),
    refetchInterval: (query) => symbolsRefetchInterval(query.state.data, pollBackfill),
  })
}

/** The trailing 24h average price for one symbol. */
export function useAverage(symbol: string | undefined) {
  const s = normalise(symbol)
  return useQuery({
    queryKey: queryKeys.average(s),
    queryFn: () => getJson<AveragePriceResponse>(apiPaths.average(s)),
    enabled: s.length > 0,
    refetchInterval: 15_000,
  })
}

/**
 * The SMA over `n` buckets of width `p`, optionally starting at `s` (ISO-8601).
 * Disabled until the symbol is set and `n` is in range, so half-typed input never hits the API.
 */
export function useSma(symbol: string | undefined, n: number, p: Period, s?: string) {
  const sym = normalise(symbol)
  const start = s?.trim() ?? ''
  return useQuery({
    queryKey: queryKeys.sma(sym, n, p, start || undefined),
    queryFn: () => getJson<SmaResponse>(apiPaths.sma(sym, n, p, start || undefined)),
    enabled: sym.length > 0 && isValidSmaN(n),
    placeholderData: keepPreviousForSymbol(sym),
    // A fixed start describes a closed window; only "up to now" moves.
    refetchInterval: start ? false : 30_000,
  })
}

/** OHLCV candles for the chart, oldest first. */
export function useCandles(symbol: string | undefined, p: Period, limit: number = CANDLES_DEFAULT_LIMIT) {
  const s = normalise(symbol)
  return useQuery({
    queryKey: queryKeys.candles(s, p, limit),
    queryFn: () => getJson<CandleResponse[]>(apiPaths.candles(s, p, limit)),
    enabled: s.length > 0,
    placeholderData: keepPreviousForSymbol(s),
    refetchInterval: p === '1m' || p === '5m' ? 15_000 : 60_000,
  })
}

/**
 * Ingestion stats over HTTP. The hub pushes the same shape every 2 s, so this is only a fallback:
 * it is off unless the caller enables it (e.g. while the hub is not connected).
 */
export function useStats(options: { enabled?: boolean } = {}) {
  return useQuery({
    queryKey: queryKeys.stats(),
    queryFn: () => getJson<SystemStatsResponse>(apiPaths.stats()),
    enabled: options.enabled ?? false,
    refetchInterval: 10_000,
  })
}
