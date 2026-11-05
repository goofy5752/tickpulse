import { QueryClientProvider, type QueryClient } from '@tanstack/react-query'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { useEffect, type ReactNode } from 'react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createQueryClient } from '@/api/queries'
import type { CandleResponse, Period, SmaResponse } from '@/api/types'
import type { PriceChartProps } from '@/components/PriceChart'
import { DEFAULT_SMA_PARAMS, type SmaParams } from '@/lib/smaParams'
import { CHART_MODE_STORAGE_KEY, CHART_TYPE_STORAGE_KEY, ChartPanel } from './ChartPanel'

// The real chart needs a canvas; a stub shows what the panel hands it and counts mounts.
const chart = vi.hoisted(() => ({ mounts: 0 }))
vi.mock('@/components/PriceChart', () => ({
  PriceChart: (props: PriceChartProps) => {
    useEffect(() => {
      chart.mounts += 1
    }, [])
    return (
      <div
        data-testid="chart"
        data-symbol={props.symbol}
        data-period={props.period}
        data-type={props.type ?? 'candles'}
        data-visible-bars={props.visibleBars ?? ''}
        data-sma={props.sma ? String(props.sma.requestedDataPoints) : ''}
        className={props.className}
      />
    )
  },
}))

function candles(): CandleResponse[] {
  return [0, 1, 2].map((day) => ({
    bucketStart: new Date(Date.UTC(2026, 10, 1 + day)).toISOString(),
    open: 100,
    high: 110,
    low: 90,
    close: 105,
    volume: 1,
  }))
}

function sma(symbol: string, n: number, p: Period): SmaResponse {
  return {
    symbol,
    period: p,
    requestedDataPoints: n,
    actualDataPoints: n,
    value: 100,
    windowStart: '2026-11-01T00:00:00Z',
    windowEnd: '2026-11-03T00:00:00Z',
    referenceTime: '2026-11-03T12:00:00Z',
    startParameter: null,
    dataPoints: [],
  }
}

type Behaviour = 'ok' | 'fail' | 'hold'

/** Per request key (`candles|BTCUSDT|1d`, `sma|BTCUSDT|20|1d|<s>`) how the fake API answers. */
let behaviour: Map<string, Behaviour>
let held: { key: string; release: () => void }[]
let fetchMock: ReturnType<typeof vi.fn<typeof fetch>>

function urlOf(input: string | URL | Request): string {
  if (typeof input === 'string') return input
  return input instanceof URL ? input.href : input.url
}

function answer(key: string, body: () => unknown): Promise<Response> {
  const mode = behaviour.get(key) ?? 'ok'
  if (mode === 'fail') {
    return Promise.resolve(
      new Response(
        JSON.stringify({ type: 'https://tickpulse.dev/problems/validation', title: 'Bad', status: 400, detail: 'p is not supported here.' }),
        { status: 400, headers: { 'Content-Type': 'application/problem+json' } },
      ),
    )
  }
  if (mode === 'hold') {
    return new Promise((resolve) => {
      held.push({ key, release: () => { resolve(Response.json(body())) } })
    })
  }
  return Promise.resolve(Response.json(body()))
}

beforeEach(() => {
  chart.mounts = 0
  behaviour = new Map()
  held = []
  fetchMock = vi.fn<typeof fetch>((input) => {
    const url = new URL(urlOf(input), 'http://localhost')
    const [, , symbol = '', endpoint] = url.pathname.split('/')
    const p = (url.searchParams.get('p') ?? '1d') as Period
    if (endpoint === 'candles') return answer(`candles|${symbol}|${p}`, candles)
    const n = Number(url.searchParams.get('n'))
    return answer(`sma|${symbol}|${n}|${p}|${url.searchParams.get('s') ?? ''}`, () => sma(symbol, n, p))
  })
  vi.stubGlobal('fetch', fetchMock)
})

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
  localStorage.clear()
})

function setup(initial: { symbol: string; params: SmaParams }, onParamsChange: (next: SmaParams) => void = vi.fn()) {
  const client: QueryClient = createQueryClient()
  const panel = (symbol: string, params: SmaParams): ReactNode => (
    <QueryClientProvider client={client}>
      <ChartPanel symbol={symbol} params={params} onParamsChange={onParamsChange} live={undefined} fallbackPrice={null} />
    </QueryClientProvider>
  )
  const view = render(panel(initial.symbol, initial.params))
  return { show: (symbol: string, params: SmaParams) => { view.rerender(panel(symbol, params)) } }
}

const daily: SmaParams = { ...DEFAULT_SMA_PARAMS, n: 20, p: '1d' }

async function chartFor(symbol: string, period: Period) {
  await waitFor(() => {
    const element = screen.getByTestId('chart')
    expect(element.dataset.symbol).toBe(symbol)
    expect(element.dataset.period).toBe(period)
  })
  return screen.getByTestId('chart')
}

function candleRequests(key: string): number {
  return fetchMock.mock.calls.filter(([input]) => {
    const url = new URL(urlOf(input), 'http://localhost')
    return url.pathname.endsWith('/candles') && `${url.pathname.split('/')[2]}|${url.searchParams.get('p')}` === key
  }).length
}

describe('ChartPanel', () => {
  it('ChartPanel_RangeOnTheSamePeriod_ShowsItsNewestBuckets', async () => {
    const onParamsChange = vi.fn()
    setup({ symbol: 'BTCUSDT', params: daily }, onParamsChange)
    await chartFor('BTCUSDT', '1d')
    // Simple mode is the default and opens on 6M.
    expect(screen.getByTestId('chart').dataset.visibleBars).toBe('183')
    expect(screen.queryByRole('radio', { name: '1w' })).toBeNull()

    fireEvent.click(screen.getByRole('radio', { name: 'Three months of daily candles' }))

    // 3M is daily already: no period change, just the newest 92 days in view.
    expect(onParamsChange).not.toHaveBeenCalled()
    expect(screen.getByTestId('chart').dataset.visibleBars).toBe('92')
    expect(screen.getByRole<HTMLInputElement>('radio', { name: 'Three months of daily candles' }).checked).toBe(true)
  })

  it('ChartPanel_RangeOnAnotherPeriod_SetsThePeriod', async () => {
    const onParamsChange = vi.fn()
    setup({ symbol: 'BTCUSDT', params: daily }, onParamsChange)
    await chartFor('BTCUSDT', '1d')

    fireEvent.click(screen.getByRole('radio', { name: 'One week of 30-minute candles' }))

    expect(onParamsChange).toHaveBeenCalledWith({ ...daily, p: '30m' })
  })

  it('ChartPanel_ProMode_ShowsCandleSizes_InsteadOfRanges_AndIsRemembered', async () => {
    const onParamsChange = vi.fn()
    setup({ symbol: 'BTCUSDT', params: daily }, onParamsChange)
    await chartFor('BTCUSDT', '1d')

    fireEvent.click(screen.getByRole('radio', { name: 'Pro' }))

    expect(screen.queryByRole('radio', { name: 'Six months of daily candles' })).toBeNull()
    expect(screen.getByTestId('chart').dataset.visibleBars).toBe('')
    fireEvent.click(screen.getByRole('radio', { name: '1w' }))
    expect(onParamsChange).toHaveBeenCalledWith({ ...daily, p: '1w' })
    expect(localStorage.getItem(CHART_MODE_STORAGE_KEY)).toBe('pro')
  })

  it('ChartPanel_ChartType_SwitchesToLine_AndIsRemembered', async () => {
    setup({ symbol: 'BTCUSDT', params: daily })
    const element = await chartFor('BTCUSDT', '1d')
    expect(element.dataset.type).toBe('candles')
    expect(screen.getByText('1d candles')).toBeTruthy()

    fireEvent.click(screen.getByRole('radio', { name: 'Line' }))

    expect(screen.getByTestId('chart').dataset.type).toBe('line')
    expect(screen.getByText('1d closes')).toBeTruthy()
    expect(localStorage.getItem(CHART_TYPE_STORAGE_KEY)).toBe('line')

    // A fresh visit opens on the remembered view.
    cleanup()
    setup({ symbol: 'BTCUSDT', params: daily })
    expect((await chartFor('BTCUSDT', '1d')).dataset.type).toBe('line')
    expect(screen.getByRole<HTMLInputElement>('radio', { name: 'Line' }).checked).toBe(true)
  })

  it('ChartPanel_ChartType_KeepsTheChartMounted', async () => {
    setup({ symbol: 'BTCUSDT', params: daily })
    await chartFor('BTCUSDT', '1d')

    fireEvent.click(screen.getByRole('radio', { name: 'Line' }))
    fireEvent.click(screen.getByRole('radio', { name: 'Candles' }))

    expect(screen.getByTestId('chart').dataset.type).toBe('candles')
    expect(chart.mounts).toBe(1)
  })

  it('ChartPanel_PeriodLoading_KeepsPreviousChartDimmedWithStatus', async () => {
    behaviour.set('candles|BTCUSDT|5m', 'hold')
    const { show } = setup({ symbol: 'BTCUSDT', params: daily })
    await chartFor('BTCUSDT', '1d')

    show('BTCUSDT', { ...daily, p: '5m' })

    expect((await screen.findByRole('status')).textContent).toBe('Loading BTCUSDT 5m candles')
    const element = screen.getByTestId('chart')
    expect(element.dataset.period).toBe('1d')
    expect(element.className).toContain('opacity-45')
    // A stale chart never gets an SMA window drawn over it.
    expect(element.dataset.sma).toBe('')
  })

  it('ChartPanel_PeriodFails_ShowsErrorWithRetry_InsteadOfEndlessLoading', async () => {
    behaviour.set('candles|BTCUSDT|5m', 'fail')
    const { show } = setup({ symbol: 'BTCUSDT', params: daily })
    await chartFor('BTCUSDT', '1d')

    show('BTCUSDT', { ...daily, p: '5m' })

    const alert = await screen.findByRole('alert')
    expect(alert.textContent).toContain('Could not load the 5m candles for BTCUSDT.')
    expect(alert.textContent).toContain('p is not supported here.')
    expect(screen.queryByText(/^Loading/)).toBeNull()

    behaviour.set('candles|BTCUSDT|5m', 'ok')
    fireEvent.click(screen.getByRole('button', { name: 'Try again' }))

    await chartFor('BTCUSDT', '5m')
    expect(screen.queryByRole('alert')).toBeNull()
    expect(candleRequests('BTCUSDT|5m')).toBe(2)
  })

  it('ChartPanel_FirstLoadFails_ShowsEmptyStateWithRetry', async () => {
    behaviour.set('candles|BTCUSDT|1d', 'fail')
    setup({ symbol: 'BTCUSDT', params: daily })

    expect(await screen.findByText('Could not load the 1d candles.')).toBeTruthy()
    expect(screen.getByRole('button', { name: 'Try again' })).toBeTruthy()
    expect(screen.queryByTestId('chart')).toBeNull()
  })

  it('ChartPanel_SymbolSwitch_KeepsTheChartMounted', async () => {
    behaviour.set('candles|ETHUSDT|1d', 'hold')
    const { show } = setup({ symbol: 'BTCUSDT', params: daily })
    await chartFor('BTCUSDT', '1d')

    show('ETHUSDT', daily)

    expect((await screen.findByRole('status')).textContent).toBe('Loading ETHUSDT 1d candles')
    expect(screen.getByTestId('chart').className).toContain('opacity-45')

    for (const request of held) request.release()
    await chartFor('ETHUSDT', '1d')
    expect(chart.mounts).toBe(1)
  })

  it('ChartPanel_SmaLoadingForNewStart_DoesNotDrawThePreviousWindow', async () => {
    const { show } = setup({ symbol: 'BTCUSDT', params: daily })
    await waitFor(() => {
      expect(screen.getByTestId('chart').dataset.sma).toBe('20')
    })

    // Same n and p, new start: the placeholder answer still says n = 20 but its window is the old one.
    behaviour.set('sma|BTCUSDT|20|1d|2026-10-01T00:00:00Z', 'hold')
    show('BTCUSDT', { ...daily, s: '2026-10-01T00:00' })

    await waitFor(() => {
      expect(held).toHaveLength(1)
    })
    expect(screen.getByTestId('chart').dataset.sma).toBe('')
    for (const request of held) request.release()
    await waitFor(() => {
      expect(screen.getByTestId('chart').dataset.sma).toBe('20')
    })
  })
})
