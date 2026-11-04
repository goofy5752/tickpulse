import { cleanup, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it } from 'vitest'
import type { SystemStatsResponse, WsStatus } from '@/api/types'
import type { HubStatus } from '@/hooks/useMarketHub'
import { StatusPill } from './StatusPill'

function stats(wsStatus: WsStatus, ingestLagMs = 38): SystemStatsResponse {
  return {
    wsStatus,
    isLeader: true,
    ticksPerSecond: 41.2,
    ingestLagMs,
    storedTicksApprox: 8_400_000,
    uptimeSeconds: 3600,
    connectedSince: null,
    lastReconnect: null,
    nextRotation: null,
    activeSymbols: 3,
    version: '0.1.0',
  }
}

afterEach(() => {
  cleanup()
})

function status(): string {
  return screen.getByRole('status').textContent
}

describe('StatusPill', () => {
  it('StatusPill_LiveFeed_AnnouncesLiveAndShowsLag', () => {
    const { container } = render(<StatusPill hubStatus="connected" stats={stats('Connected')} />)

    expect(status()).toBe('Binance stream live')
    expect(container.textContent).toContain('38 ms')
    expect(container.querySelector('.text-down')).toBeNull()
  })

  it('StatusPill_SlowLag_TurnsTheLagRed', () => {
    const { container } = render(<StatusPill hubStatus="connected" stats={stats('Connected', 7_200)} />)

    expect(container.querySelector('.text-down')?.textContent).toContain('7.2 s')
  })

  it.each<[HubStatus, SystemStatsResponse | null, string]>([
    ['connecting', null, 'Connecting to the live feed'],
    ['connected', null, 'Connecting to the live feed'],
    ['reconnecting', stats('Connected'), 'Reconnecting to the live feed'],
    ['disconnected', null, 'The dashboard cannot reach the API'],
    ['connected', stats('Connecting'), 'The API is connecting to Binance'],
    ['connected', stats('Standby'), 'This API instance is on standby'],
    ['connected', stats('Disconnected'), 'The Binance stream is disconnected'],
    // Polled stats stand in for the hub while it is down.
    ['disconnected', stats('Connected'), 'Binance stream live'],
  ])('StatusPill_Hub_%s_AnnouncesState', (hub, data, expected) => {
    render(<StatusPill hubStatus={hub} stats={data} />)

    expect(status()).toBe(expected)
  })

  it('StatusPill_IsATipTrigger_WithTheLiveRegionOutsideIt', () => {
    render(<StatusPill hubStatus="connected" stats={stats('Connected')} />)

    const trigger = screen.getByRole('button', { name: 'Binance stream live, ingest lag 38 ms' })
    expect(trigger.dataset.help).toBe('navStatus')
    expect(trigger.contains(screen.getByRole('status'))).toBe(false)
  })

  it('StatusPill_NotLive_HidesLag', () => {
    const { container } = render(<StatusPill hubStatus="connected" stats={stats('Disconnected')} />)

    expect(container.textContent).not.toContain('ms')
    expect(container.textContent).toContain('Feed down')
  })
})
