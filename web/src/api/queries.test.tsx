import { QueryClientProvider } from '@tanstack/react-query'
import { renderHook, waitFor } from '@testing-library/react'
import type { ReactNode } from 'react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import {
  BACKFILL_POLL_MS,
  createQueryClient,
  symbolsRefetchInterval,
  SYMBOLS_REFRESH_MS,
  useSma,
  useStats,
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
