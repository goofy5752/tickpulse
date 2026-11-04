import { QueryClientProvider } from '@tanstack/react-query'
import { renderHook, waitFor } from '@testing-library/react'
import type { ReactNode } from 'react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import {
  BACKFILL_POLL_MS,
  createQueryClient,
  symbolsRefetchInterval,
  useAverage,
  useCandles,
  SYMBOLS_REFRESH_MS,
  useSma,
  useStats,
  useSymbols,
} from './queries'
import type { BackfillStatus, Period, SmaResponse, SymbolResponse } from './types'

function sma(symbol: string, n: number): SmaResponse {
  return {
    symbol,
    period: '1d',
    requestedDataPoints: n,
    actualDataPoints: n,
    value: n,
    windowStart: '2026-10-14T00:00:00Z',
    windowEnd: '2026-11-03T00:00:00Z',
    referenceTime: '2026-11-03T12:00:00Z',
    startParameter: null,
    dataPoints: [],
  }
}

function urlOf(input: string | URL | Request): string {
  if (typeof input === 'string') return input
  return input instanceof URL ? input.href : input.url
}

let fetchMock: ReturnType<typeof vi.fn<typeof fetch>>
/** Requests that are still waiting for an answer, so tests decide when each one resolves. */
let pending: { url: string; resolve: (response: Response) => void }[]

beforeEach(() => {
  pending = []
  fetchMock = vi.fn<typeof fetch>(
    (input) =>
      new Promise<Response>((resolve) => {
        pending.push({ url: urlOf(input), resolve })
      }),
  )
  vi.stubGlobal('fetch', fetchMock)
})

afterEach(() => {
  vi.unstubAllGlobals()
})

let client = createQueryClient()

function wrapper({ children }: { children: ReactNode }) {
  return <QueryClientProvider client={client}>{children}</QueryClientProvider>
}

function answer(index: number, body: unknown) {
  const request = pending[index]
  if (!request) throw new Error(`No pending request #${index}`)
  request.resolve(Response.json(body))
}

describe('useStats', () => {
  beforeEach(() => {
    client = createQueryClient()
  })

  it('useStats_ByDefault_DoesNotFetch', () => {
    const { result } = renderHook(() => useStats(), { wrapper })

    expect(result.current.fetchStatus).toBe('idle')
    expect(fetchMock).not.toHaveBeenCalled()
  })

  it('useStats_Enabled_FetchesStats', async () => {
    renderHook(() => useStats({ enabled: true }), { wrapper })

    await waitFor(() => {
      expect(fetchMock).toHaveBeenCalledOnce()
    })
    expect(pending[0]?.url).toBe('/api/system/stats')
  })
})

describe('useSma', () => {
  beforeEach(() => {
    client = createQueryClient()
  })

  interface Props {
    symbol: string
    n: number
    p: Period
  }

  it('useSma_InvalidN_DoesNotFetch', () => {
    renderHook(() => useSma('BTCUSDT', 0, '1d'), { wrapper })
    renderHook(() => useSma('BTCUSDT', 1001, '1d'), { wrapper })
    renderHook(() => useSma('BTCUSDT', 2.5, '1d'), { wrapper })

    expect(fetchMock).not.toHaveBeenCalled()
  })

  it('useSma_SameSymbolNewN_KeepsPreviousResultWhileLoading', async () => {
    const { result, rerender } = renderHook(({ symbol, n, p }: Props) => useSma(symbol, n, p), {
      wrapper,
      initialProps: { symbol: 'BTCUSDT', n: 20, p: '1d' },
    })
    await waitFor(() => {
      expect(pending).toHaveLength(1)
    })
    answer(0, sma('BTCUSDT', 20))
    await waitFor(() => {
      expect(result.current.data?.requestedDataPoints).toBe(20)
    })

    rerender({ symbol: 'BTCUSDT', n: 30, p: '1d' })

    await waitFor(() => {
      expect(pending).toHaveLength(2)
    })
    expect(result.current.isPlaceholderData).toBe(true)
    expect(result.current.data?.requestedDataPoints).toBe(20)
  })

  it('useSma_OtherSymbol_DoesNotShowPreviousSymbolsResult', async () => {
    const { result, rerender } = renderHook(({ symbol, n, p }: Props) => useSma(symbol, n, p), {
      wrapper,
      initialProps: { symbol: 'BTCUSDT', n: 20, p: '1d' },
    })
    await waitFor(() => {
      expect(pending).toHaveLength(1)
    })
    answer(0, sma('BTCUSDT', 20))
    await waitFor(() => {
      expect(result.current.data?.symbol).toBe('BTCUSDT')
    })

    rerender({ symbol: 'ETHUSDT', n: 20, p: '1d' })

    await waitFor(() => {
      expect(pending).toHaveLength(2)
    })
    expect(result.current.data).toBeUndefined()
    expect(result.current.isPending).toBe(true)
  })
})

describe('symbolsRefetchInterval', () => {
  const row = (backfillStatus: BackfillStatus, isActive = true): SymbolResponse => ({
    symbol: 'SOLUSDT',
    isActive,
    latestPrice: null,
    change24hPercent: null,
    backfillStatus,
  })

  it('polls fast while an active symbol is backfilling, only when asked to', () => {
    expect(symbolsRefetchInterval([row('Pending')], true)).toBe(BACKFILL_POLL_MS)
    expect(symbolsRefetchInterval([row('Running')], true)).toBe(BACKFILL_POLL_MS)
    expect(symbolsRefetchInterval([row('Running')], false)).toBe(SYMBOLS_REFRESH_MS)
  })

  it('settles back to the normal cadence once backfills finish', () => {
    expect(symbolsRefetchInterval([row('Completed'), row('Failed'), row('NotRequested')], true)).toBe(SYMBOLS_REFRESH_MS)
    expect(symbolsRefetchInterval([row('Running', false)], true)).toBe(SYMBOLS_REFRESH_MS)
    expect(symbolsRefetchInterval(undefined, true)).toBe(SYMBOLS_REFRESH_MS)
  })
})

/**
 * Superseded and unmounted reads must never be aborted. Browser extensions commonly wrap
 * `window.fetch` and watch the returned promise without a rejection handler; every abort then
 * surfaces in the console as an uncaught `AbortError`, whatever the app does with its own copy.
 */
describe('in-flight requests', () => {
  let signals: AbortSignal[]
  /** What a fetch wrapper without a rejection handler would leak as uncaught errors. */
  let unhandled: unknown[]

  beforeEach(() => {
    client = createQueryClient()
    signals = []
    unhandled = []
    // Behaves like the browser's fetch (rejects with the signal's reason on abort), seen through
    // a wrapper that observes the response the way such extensions do. The wrapper's own promise
    // records its rejections here instead of leaking them, so the assertions can see them.
    fetchMock = vi.fn<typeof fetch>((input, init) => {
      const signal = init?.signal ?? undefined
      if (signal) signals.push(signal)
      const promise = new Promise<Response>((resolve, reject) => {
        pending.push({ url: urlOf(input), resolve })
        signal?.addEventListener('abort', () => {
          reject(signal.reason as Error)
        })
      })
      promise
        .then((response) => response.clone().text())
        .catch((error: unknown) => {
          unhandled.push(error)
        })
      return promise
    })
    vi.stubGlobal('fetch', fetchMock)
  })

  /** Lets every rejection settle before the assertions. */
  const settle = () => new Promise((resolve) => setTimeout(resolve, 20))

  it('useSma_NTypedWhileTheLastDigitsRequestIsInFlight_DoesNotAbortIt', async () => {
    const { result, rerender } = renderHook(({ n }: { n: number }) => useSma('BTCUSDT', n, '1d'), {
      wrapper,
      initialProps: { n: 2 },
    })
    await waitFor(() => {
      expect(pending).toHaveLength(1)
    })

    rerender({ n: 25 })
    await waitFor(() => {
      expect(pending).toHaveLength(2)
    })
    await settle()

    expect(signals.filter((s) => s.aborted)).toHaveLength(0)
    expect(unhandled).toEqual([])

    answer(1, sma('BTCUSDT', 25))
    answer(0, sma('BTCUSDT', 2))
    await waitFor(() => {
      expect(result.current.data?.requestedDataPoints).toBe(25)
    })
    expect(result.current.isError).toBe(false)
    // The superseded answer still lands in the cache, ready if the user types 2 again.
    expect(client.getQueryData(['sma', 'BTCUSDT', 2, '1d', null])).toMatchObject({ requestedDataPoints: 2 })
  })

  it('useCandles_PeriodSwitchedWhileLoading_DoesNotAbortTheRequest', async () => {
    const { rerender } = renderHook(({ p }: { p: Period }) => useCandles('BTCUSDT', p, 300), {
      wrapper,
      initialProps: { p: '1w' },
    })
    await waitFor(() => {
      expect(pending).toHaveLength(1)
    })

    rerender({ p: '1m' })
    await waitFor(() => {
      expect(pending).toHaveLength(2)
    })
    await settle()

    expect(signals.filter((s) => s.aborted)).toHaveLength(0)
    expect(unhandled).toEqual([])
  })

  it.each<[string, () => unknown]>([
    ['useSymbols', () => useSymbols()],
    ['useAverage', () => useAverage('BTCUSDT')],
    ['useSma', () => useSma('BTCUSDT', 20, '1d')],
    ['useCandles', () => useCandles('BTCUSDT', '1d')],
    ['useStats', () => useStats({ enabled: true })],
  ])('%s_UnmountedWhileLoading_DoesNotAbortTheRequest', async (_name, hook) => {
    const { unmount } = renderHook(hook, { wrapper })
    await waitFor(() => {
      expect(pending).toHaveLength(1)
    })

    unmount()
    await settle()

    expect(signals.filter((s) => s.aborted)).toHaveLength(0)
    expect(unhandled).toEqual([])
  })
})
