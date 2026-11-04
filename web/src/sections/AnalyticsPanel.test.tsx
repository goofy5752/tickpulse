import { QueryClientProvider } from '@tanstack/react-query'
import { cleanup, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { useState } from 'react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createQueryClient } from '@/api/queries'
import type { AveragePriceResponse, SmaResponse } from '@/api/types'
import { DEFAULT_SMA_PARAMS, type SmaParams } from '@/lib/smaParams'
import { AnalyticsPanel } from './AnalyticsPanel'

const average: AveragePriceResponse = {
  symbol: 'BTCUSDT',
  averagePrice: 96880.14,
  from: '2026-11-02T12:00:00Z',
  to: '2026-11-03T12:00:00Z',
  coveredSeconds: 86_280,
  sampleCount: 1_204_331,
  windowHours: 24,
}

function smaResponse(n: number): SmaResponse {
  return {
    symbol: 'BTCUSDT',
    period: '1d',
    requestedDataPoints: n,
    actualDataPoints: 12,
    value: 94102.77,
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

beforeEach(() => {
  fetchMock = vi.fn<typeof fetch>((input) => {
    const url = new URL(urlOf(input), 'http://localhost')
    if (url.pathname.endsWith('/24hAvgPrice')) return Promise.resolve(Response.json(average))
    if (url.pathname.endsWith('/SimpleMovingAverage')) {
      return Promise.resolve(Response.json(smaResponse(Number(url.searchParams.get('n')))))
    }
    return Promise.resolve(new Response(null, { status: 404 }))
  })
  vi.stubGlobal('fetch', fetchMock)
})

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
})

/** The panel is controlled; the dashboard owns the parameters, so the test does too. */
function Harness({ initial }: { initial: SmaParams }) {
  const [params, setParams] = useState(initial)
  return <AnalyticsPanel symbol="BTCUSDT" params={params} onParamsChange={setParams} />
}

function renderPanel(initial: SmaParams = { ...DEFAULT_SMA_PARAMS, n: 20, p: '1d' }) {
  const client = createQueryClient()
  return render(
    <QueryClientProvider client={client}>
      <Harness initial={initial} />
    </QueryClientProvider>,
  )
}

function smaRequests(): URL[] {
  return fetchMock.mock.calls
    .map(([input]) => new URL(urlOf(input), 'http://localhost'))
    .filter((url) => url.pathname.endsWith('/SimpleMovingAverage'))
}

describe('AnalyticsPanel', () => {
  it('AnalyticsPanel_ShowsActualOverRequested', async () => {
    renderPanel()

    expect(await screen.findByText('12 / 20 points')).toBeTruthy()
    expect(smaRequests()[0]?.searchParams.get('n')).toBe('20')
    expect(smaRequests()[0]?.searchParams.get('p')).toBe('1d')
  })

  it('AnalyticsPanel_InvalidN_DisablesQuery', async () => {
    const user = userEvent.setup()
    renderPanel()
    await screen.findByText('12 / 20 points')

    const input = screen.getByLabelText(/points \(n\)/i)
    await user.clear(input)
    await user.type(input, '0')

    expect(input.getAttribute('aria-invalid')).toBe('true')
    expect(screen.getByText(/whole number from 1 to 1000/i)).toBeTruthy()
    // Give any (wrongly) enabled query time to fire before checking.
    await new Promise((resolve) => setTimeout(resolve, 50))
    const requestedN = smaRequests().map((url) => url.searchParams.get('n'))
    expect(requestedN).not.toContain('0')
    expect(requestedN).not.toContain('')
    expect(screen.queryByText('12 / 20 points')).toBeNull()
  })

  it('AnalyticsPanel_AverageCard_ShowsCoverageAndSampleCount', async () => {
    renderPanel()

    expect(await screen.findByText('1,204,331')).toBeTruthy()
    expect(screen.getByText('23h 58m of 24h')).toBeTruthy()
  })

  it('AnalyticsPanel_ValidNAgain_RefetchesWithNewN', async () => {
    const user = userEvent.setup()
    renderPanel()
    await screen.findByText('12 / 20 points')

    const input = screen.getByLabelText(/points \(n\)/i)
    await user.clear(input)
    await user.type(input, '30')

    expect(await screen.findByText('12 / 30 points')).toBeTruthy()
    await waitFor(() => {
      expect(smaRequests().at(-1)?.searchParams.get('n')).toBe('30')
    })
  })
})
