import { QueryClientProvider } from '@tanstack/react-query'
import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import type { ReactNode } from 'react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createQueryClient } from '@/api/queries'
import type { CandleResponse, LivePrice, SymbolResponse } from '@/api/types'
import { TickerTiles } from './TickerTiles'

const btc: SymbolResponse = {
  symbol: 'BTCUSDT',
  isActive: true,
  latestPrice: 97_000,
  change24hPercent: 1.84,
  backfillStatus: 'Completed',
}

const minutes: CandleResponse[] = Array.from({ length: 30 }, (_, i) => ({
  bucketStart: new Date(Date.UTC(2026, 10, 3, 12, i * 2)).toISOString(),
  open: 97_000 + i,
  high: 97_000 + i,
  low: 97_000 + i,
  close: 97_000 + i,
  volume: 1,
}))

function live(price: number): LivePrice {
  return { symbol: 'BTCUSDT', price, tradeTime: '2026-11-03T13:00:30Z' }
}

function stubReducedMotion(reduce: boolean) {
  vi.stubGlobal(
    'matchMedia',
    vi.fn((query: string) => ({
      matches: reduce && query.includes('reduce'),
      media: query,
      addEventListener: vi.fn(),
      removeEventListener: vi.fn(),
    })),
  )
}

beforeEach(() => {
  vi.stubGlobal(
    'fetch',
    vi.fn<typeof fetch>(() => Promise.resolve(Response.json(minutes))),
  )
})

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
})

function setup(onSelect = vi.fn()) {
  const client = createQueryClient()
  const wrap = (node: ReactNode) => <QueryClientProvider client={client}>{node}</QueryClientProvider>
  const tiles = (price: LivePrice | undefined) =>
    wrap(
      <TickerTiles
        symbols={[btc]}
        isLoading={false}
        error={null}
        prices={price ? { BTCUSDT: price } : {}}
        selected="BTCUSDT"
        onSelect={onSelect}
      />,
    )
  const view = render(tiles(undefined))
  const update = (price: LivePrice) => {
    view.rerender(tiles(price))
  }
  return { ...view, update, onSelect }
}

function priceElement(): HTMLElement {
  const element = screen.getByText(/^\$9[67],\d{3}\.\d{2}$/).closest('span')
  if (!element) throw new Error('price not rendered')
  return element
}

describe('TickerTiles', () => {
  it('Tile_PriceRises_FlashesUp_ThenDown', () => {
    stubReducedMotion(false)
    const { update } = setup()

    update(live(97_100))
    expect(priceElement().className).toContain('price-flash-up')

    update(live(96_900))
    expect(priceElement().className).toContain('price-flash-down')
  })

  it('Tile_ReducedMotion_NeverFlashes', () => {
    stubReducedMotion(true)
    const { update } = setup()

    update(live(97_100))
    update(live(96_900))

    expect(priceElement().className).not.toMatch(/price-flash/)
    expect(priceElement().textContent).toBe('$96,900.00')
  })

  it('Tile_Click_SelectsItsSymbol', () => {
    stubReducedMotion(false)
    const { onSelect } = setup()

    fireEvent.click(screen.getByRole('button', { pressed: true }))

    expect(onSelect).toHaveBeenCalledWith('BTCUSDT')
  })

  it('Tile_Sparkline_LabelsTheRealSpan', async () => {
    stubReducedMotion(false)
    setup()

    // 30 buckets two minutes apart: 12:00 → 12:58 plus that last minute, 59 minutes in all.
    expect(await screen.findByRole('img', { name: /^Past 59m, \+0\.03%$/ })).toBeTruthy()
  })
})
