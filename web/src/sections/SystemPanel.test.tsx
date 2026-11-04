import { cleanup, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { SystemStatsResponse } from '@/api/types'
import { SystemPanel } from './SystemPanel'

const NOW = Date.parse('2026-11-04T12:00:00Z')

const stats: SystemStatsResponse = {
  wsStatus: 'Connected',
  isLeader: true,
  ticksPerSecond: 41.2,
  ingestLagMs: 38,
  storedTicksApprox: 8_412_345,
  uptimeSeconds: 2 * 86_400 + 4 * 3600,
  connectedSince: '2026-11-04T09:00:00Z',
  lastReconnect: '2026-11-04T09:00:00Z',
  nextRotation: '2026-11-05T08:00:00Z',
  activeSymbols: 3,
  version: '0.1.0',
}

afterEach(() => {
  cleanup()
  vi.useRealTimers()
})

describe('SystemPanel', () => {
  it('SystemPanel_ShowsStatsAndRotation', () => {
    vi.useFakeTimers({ now: NOW, toFake: ['Date'] })
    render(<SystemPanel stats={stats} hubStatus="connected" />)

    expect(screen.getByText('41.2')).toBeTruthy()
    expect(screen.getByText('38 ms')).toBeTruthy()
    expect(screen.getByText('8.4M')).toBeTruthy()
    expect(screen.getByText('2d 4h')).toBeTruthy()
    expect(screen.getByText('3h 00m ago')).toBeTruthy()
    expect(screen.getByText('in 20h 00m')).toBeTruthy()
    // 3 h of the 23 h connection lifetime have passed.
    const meter = screen.getByRole('meter', { name: /connection lifetime/i })
    expect(meter.getAttribute('aria-valuenow')).toBe('13')
  })

  it('SystemPanel_NoStats_SaysWhy', () => {
    render(<SystemPanel stats={null} hubStatus="disconnected" />)
    expect(screen.getByText(/the api is not answering/i)).toBeTruthy()
  })
})
