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

/** All monitored symbols with their latest price and 24h change. */
export function useSymbols() {
  return useQuery({
    queryKey: queryKeys.symbols(),
    queryFn: ({ signal }) => getJson<SymbolResponse[]>(apiPaths.symbols(), { signal }),
    refetchInterval: 30_000,
  })
}

/** The trailing 24h average price for one symbol. */
export function useAverage(symbol: string | undefined) {
  const s = normalise(symbol)
  return useQuery({
    queryKey: queryKeys.average(s),
    queryFn: ({ signal }) => getJson<AveragePriceResponse>(apiPaths.average(s), { signal }),
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
    queryFn: ({ signal }) => getJson<SmaResponse>(apiPaths.sma(sym, n, p, start || undefined), { signal }),
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
    queryFn: ({ signal }) => getJson<CandleResponse[]>(apiPaths.candles(s, p, limit), { signal }),
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
    queryFn: ({ signal }) => getJson<SystemStatsResponse>(apiPaths.stats(), { signal }),
    enabled: options.enabled ?? false,
    refetchInterval: 10_000,
  })
}
