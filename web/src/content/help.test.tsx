import { QueryClientProvider } from '@tanstack/react-query'
import { cleanup, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import type { ReactNode } from 'react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createQueryClient } from '@/api/queries'
import type { AveragePriceResponse, CandleResponse, SmaResponse, SymbolResponse, SystemStatsResponse } from '@/api/types'
import { DEFAULT_SMA_PARAMS } from '@/lib/smaParams'
import { AdminSymbols } from '@/sections/AdminSymbols'
import { AnalyticsPanel } from '@/sections/AnalyticsPanel'
import { ApiPlayground } from '@/sections/ApiPlayground'
import { ChartPanel } from '@/sections/ChartPanel'
import { Footer } from '@/sections/Footer'
import { Nav } from '@/sections/Nav'
import { SystemPanel } from '@/sections/SystemPanel'
import { TickerTiles } from '@/sections/TickerTiles'
import { HELP, HELP_KEYS, type HelpKey } from './help'

// The candle chart draws on a canvas jsdom lacks; its readout tip is covered by the source scan.
vi.mock('@/components/PriceChart', () => ({ PriceChart: () => <div data-testid="chart" /> }))

/** Tips that only render inside the canvas chart. */
const CHART_ONLY: readonly HelpKey[] = ['chartOhlc']

// ---- Fixtures -------------------------------------------------------------------------------------

const symbols: SymbolResponse[] = [
  { symbol: 'BTCUSDT', isActive: true, latestPrice: 77_500, change24hPercent: 1.2, backfillStatus: 'Completed' },
  { symbol: 'ETHUSDT', isActive: true, latestPrice: 3_100, change24hPercent: null, backfillStatus: 'Running' },
]

const average: AveragePriceResponse = {
  symbol: 'BTCUSDT',
  averagePrice: 77_400,
  from: '2026-11-03T12:00:00Z',
  to: '2026-11-04T12:00:00Z',
  coveredSeconds: 86_000,
  sampleCount: 1_000,
  windowHours: 24,
}

function sma(n: number): SmaResponse {
  return {
    symbol: 'BTCUSDT',
    period: '1d',
    requestedDataPoints: n,
    actualDataPoints: 2,
    value: 77_000,
    windowStart: '2026-11-01T00:00:00Z',
    windowEnd: '2026-11-03T00:00:00Z',
    referenceTime: '2026-11-04T12:00:00Z',
    startParameter: null,
    dataPoints: [
      { bucketStart: '2026-11-01T00:00:00Z', close: 76_900 },
      { bucketStart: '2026-11-02T00:00:00Z', close: 77_100 },
    ],
  }
}

const candles: CandleResponse[] = [0, 1, 2].map((day) => ({
  bucketStart: new Date(Date.UTC(2026, 10, 1 + day)).toISOString(),
  open: 77_000,
  high: 78_000,
  low: 76_000,
  close: 77_500,
  volume: 1,
}))

const stats: SystemStatsResponse = {
  wsStatus: 'Connected',
  isLeader: true,
  ticksPerSecond: 40,
  ingestLagMs: 120,
  storedTicksApprox: 1_000_000,
  uptimeSeconds: 3_600,
  connectedSince: '2026-11-04T09:00:00Z',
  lastReconnect: '2026-11-04T09:00:00Z',
  nextRotation: '2026-11-05T08:00:00Z',
  activeSymbols: 2,
  version: '0.1.0',
}

function urlOf(input: string | URL | Request): string {
  if (typeof input === 'string') return input
  return input instanceof URL ? input.href : input.url
}

beforeEach(() => {
  vi.stubGlobal(
    'fetch',
    vi.fn<typeof fetch>((input) => {
      const url = new URL(urlOf(input), 'http://localhost')
      const path = url.pathname
      if (path === '/api/symbols') return Promise.resolve(Response.json(symbols))
      if (path === '/api/system/stats') return Promise.resolve(Response.json(stats))
      if (path.endsWith('/24hAvgPrice')) return Promise.resolve(Response.json(average))
      if (path.endsWith('/SimpleMovingAverage')) return Promise.resolve(Response.json(sma(Number(url.searchParams.get('n')))))
      if (path.endsWith('/candles')) return Promise.resolve(Response.json(candles))
      return Promise.resolve(new Response(null, { status: 404 }))
    }),
  )
})

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
})

function withQueries(node: ReactNode) {
  return render(<QueryClientProvider client={createQueryClient()}>{node}</QueryClientProvider>)
}

function renderedTips(): Set<string> {
  return new Set(Array.from(document.querySelectorAll<HTMLElement>('[data-help]'), (el) => el.dataset.help ?? ''))
}

async function expectTips(keys: readonly HelpKey[]) {
  await waitFor(() => {
    const shown = renderedTips()
    expect(keys.filter((key) => !shown.has(key))).toEqual([])
  })
}

// ---- What every section must explain -------------------------------------------------------------

const SECTIONS: Record<string, readonly HelpKey[]> = {
  nav: ['navStatus', 'navTheme'],
  tiles: ['tilesSection', 'tilePrice', 'tileChange', 'tileSparkline', 'tileAdd'],
  chart: ['chartSection', 'chartPeriod', 'chartCandles', 'chartSma', 'chartWindow'],
  analytics: [
    'avgPrice',
    'avgCoverage',
    'avgTrades',
    'smaSection',
    'smaN',
    'smaP',
    'smaS',
    'smaNow',
    'smaValue',
    'smaDots',
    'smaPoints',
    'smaWindow',
    'smaRequest',
  ],
  playground: [
    'playSection',
    'playAccept',
    'playEndpoint',
    'playSymbol',
    'playRequest',
    'playCurl',
    'playStatus',
    'playDuration',
    'playSize',
    'playContentType',
    'playBody',
  ],
  playgroundSma: ['playN', 'playP', 'playS'],
  playgroundCandles: ['playCandlesP', 'playLimit'],
  admin: ['adminSection', 'adminKey', 'adminSymbol', 'adminAdd', 'adminActive', 'adminBackfill', 'adminRemove'],
  system: [
    'systemSection',
    'systemTicks',
    'systemLag',
    'systemStored',
    'systemUptime',
    'systemConnection',
    'systemLastReconnect',
    'systemNextRotation',
  ],
  footer: ['footerRead', 'footerUse'],
}

const sectionTips = (name: string): readonly HelpKey[] => SECTIONS[name] ?? []

describe('help content', () => {
  it('Help_EveryKey_BelongsToASection', () => {
    const assigned = new Set<HelpKey>([...Object.values(SECTIONS).flat(), ...CHART_ONLY])
    expect(HELP_KEYS.filter((key) => !assigned.has(key))).toEqual([])
    expect([...assigned].filter((key) => !(key in HELP))).toEqual([])
  })

  it('Help_EveryKey_IsUsedInTheSource', () => {
    const sources = import.meta.glob<string>(['/src/**/*.tsx', '!/src/**/*.test.tsx'], {
      query: '?raw',
      import: 'default',
      eager: true,
    })
    const code = Object.values(sources).join('\n')
    const unused = HELP_KEYS.filter((key) => !new RegExp(`["']${key}["']`).test(code))
    expect(unused).toEqual([])
  })

  it('Help_Entries_AreShortWhereTheyShouldBe', () => {
    for (const key of HELP_KEYS) {
      const entry = HELP[key]
      expect(entry.title.length, key).toBeLessThanOrEqual(28)
      expect(entry.summary.length, key).toBeGreaterThan(0)
      expect(entry.summary.length, key).toBeLessThanOrEqual(130)
    }
  })
})

describe('every section has its tips', () => {
  it('Nav', async () => {
    render(<Nav hubStatus="connected" stats={stats} />)
    await expectTips(sectionTips('nav'))
  })

  it('TickerTiles', async () => {
    withQueries(<TickerTiles symbols={symbols} isLoading={false} error={null} prices={{}} selected="BTCUSDT" onSelect={() => undefined} />)
    await expectTips(sectionTips('tiles'))
  })

  it('ChartPanel', async () => {
    withQueries(
      <ChartPanel symbol="BTCUSDT" params={DEFAULT_SMA_PARAMS} onParamsChange={() => undefined} live={undefined} fallbackPrice={77_500} />,
    )
    await expectTips(sectionTips('chart'))
  })

  it('AnalyticsPanel', async () => {
    withQueries(<AnalyticsPanel symbol="BTCUSDT" params={DEFAULT_SMA_PARAMS} onParamsChange={() => undefined} />)
    await expectTips(sectionTips('analytics'))
  })

  it('ApiPlayground, for every endpoint', async () => {
    const user = userEvent.setup()
    withQueries(<ApiPlayground symbol="BTCUSDT" symbols={['BTCUSDT', 'ETHUSDT']} />)
    await expectTips(sectionTips('playground'))
    await user.click(screen.getByLabelText('Moving average'))
    await expectTips(sectionTips('playgroundSma'))
    await user.click(screen.getByLabelText('Candles'))
    await expectTips(sectionTips('playgroundCandles'))
  })

  it('AdminSymbols', async () => {
    withQueries(<AdminSymbols />)
    await expectTips(sectionTips('admin'))
  })

  it('SystemPanel', async () => {
    render(<SystemPanel stats={stats} hubStatus="connected" />)
    await expectTips(sectionTips('system'))
  })

  it('Footer', async () => {
    render(<Footer />)
    await expectTips(sectionTips('footer'))
  })
})
