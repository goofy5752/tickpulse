import type * as SignalR from '@microsoft/signalr'
import { HubConnectionState } from '@microsoft/signalr'
import { act, renderHook, waitFor } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import type { LivePrice, SystemStatsResponse } from '@/api/types'
import { useMarketHub } from './useMarketHub'

type Handler = (...args: unknown[]) => void

/** Stands in for a SignalR HubConnection; tests drive its lifecycle callbacks by hand. */
class FakeHubConnection {
  state: HubConnectionState = HubConnectionState.Disconnected
  private readonly handlers = new Map<string, Handler>()
  private reconnectingCallback: ((error?: Error) => void) | undefined
  private reconnectedCallback: ((connectionId?: string) => void) | undefined

  // Like the real client: Connecting while the handshake runs, Connected once start() resolves.
  start = vi.fn(async () => {
    this.state = HubConnectionState.Connecting
    await Promise.resolve()
    this.state = HubConnectionState.Connected
  })

  stop = vi.fn(() => {
    this.state = HubConnectionState.Disconnected
    return Promise.resolve()
  })

  invoke = vi.fn((_method: string, ..._args: unknown[]) => Promise.resolve(undefined))

  on(method: string, handler: Handler) {
    this.handlers.set(method.toLowerCase(), handler)
  }

  off(method: string) {
    this.handlers.delete(method.toLowerCase())
  }

  onreconnecting(callback: (error?: Error) => void) {
    this.reconnectingCallback = callback
  }

  onreconnected(callback: (connectionId?: string) => void) {
    this.reconnectedCallback = callback
  }

  onclose(_callback: (error?: Error) => void) {
    // The hook's retry policy never gives up, so close only follows an explicit stop.
  }

  emit(method: string, ...args: unknown[]) {
    this.handlers.get(method.toLowerCase())?.(...args)
  }

  simulateReconnecting(error: Error) {
    this.state = HubConnectionState.Reconnecting
    this.reconnectingCallback?.(error)
  }

  simulateReconnected(connectionId: string) {
    this.state = HubConnectionState.Connected
    this.reconnectedCallback?.(connectionId)
  }

  invokedWith(method: string): unknown[] {
    return this.invoke.mock.calls.filter(([m]) => m === method).map(([, symbol]) => symbol)
  }
}

const hub = vi.hoisted(() => ({
  connections: [] as unknown[],
  urls: [] as string[],
}))

vi.mock('@microsoft/signalr', async (importOriginal) => {
  const actual = await importOriginal<typeof SignalR>()
  class HubConnectionBuilder {
    withUrl(url: string) {
      hub.urls.push(url)
      return this
    }
    withAutomaticReconnect() {
      return this
    }
    configureLogging() {
      return this
    }
    build() {
      const connection = new FakeHubConnection()
      hub.connections.push(connection)
      return connection
    }
  }
  return { ...actual, HubConnectionBuilder }
})

function latestConnection(): FakeHubConnection {
  const connection = hub.connections.at(-1)
  if (!(connection instanceof FakeHubConnection)) throw new Error('No hub connection was built')
  return connection
}

beforeEach(() => {
  hub.connections.length = 0
  hub.urls.length = 0
})

describe('useMarketHub', () => {
  it('useMarketHub_ResubscribesAfterReconnect', async () => {
    const { result } = renderHook(() => useMarketHub(['BTCUSDT', 'ethusdt']))

    await waitFor(() => {
      expect(result.current.status).toBe('connected')
    })
    const connection = latestConnection()
    expect(hub.urls).toEqual(['/hubs/market'])
    expect(connection.invokedWith('Subscribe')).toEqual(['BTCUSDT', 'ETHUSDT'])

    connection.invoke.mockClear()
    act(() => {
      connection.simulateReconnecting(new Error('socket dropped'))
    })
    expect(result.current.status).toBe('reconnecting')

    act(() => {
      connection.simulateReconnected('new-connection-id')
    })

    await waitFor(() => {
      expect(result.current.status).toBe('connected')
    })
    // A reconnect gets a new connection id, so the server-side groups must be joined again.
    await waitFor(() => {
      expect(connection.invokedWith('Subscribe')).toEqual(['BTCUSDT', 'ETHUSDT'])
    })
  })

  it('useMarketHub_PriceAndStatsMessages_UpdateState', async () => {
    const { result } = renderHook(() => useMarketHub(['BTCUSDT']))
    await waitFor(() => {
      expect(result.current.status).toBe('connected')
    })
    const connection = latestConnection()

    const price: LivePrice = { symbol: 'BTCUSDT', price: 97412.3, tradeTime: '2026-11-03T12:00:00Z' }
    const stats = { wsStatus: 'Connected', ticksPerSecond: 41.2 } as SystemStatsResponse
    act(() => {
      connection.emit('price', price)
      connection.emit('stats', stats)
    })

    expect(result.current.prices).toEqual({ BTCUSDT: price })
    expect(result.current.stats).toEqual(stats)
  })

  it('useMarketHub_SymbolsChange_SubscribesAddedAndUnsubscribesRemoved', async () => {
    const { result, rerender } = renderHook(({ symbols }) => useMarketHub(symbols), {
      initialProps: { symbols: ['BTCUSDT', 'ETHUSDT'] },
    })
    await waitFor(() => {
      expect(result.current.status).toBe('connected')
    })
    const connection = latestConnection()
    act(() => {
      connection.emit('price', { symbol: 'ETHUSDT', price: 3611.08, tradeTime: '2026-11-03T12:00:00Z' })
    })
    connection.invoke.mockClear()

    rerender({ symbols: ['BTCUSDT', 'ADAUSDT'] })

    await waitFor(() => {
      expect(connection.invokedWith('Subscribe')).toEqual(['ADAUSDT'])
    })
    expect(connection.invokedWith('Unsubscribe')).toEqual(['ETHUSDT'])
    expect(result.current.prices.ETHUSDT).toBeUndefined()
    expect(hub.connections).toHaveLength(1)
  })

  it('useMarketHub_Unmount_StopsConnection', async () => {
    const { result, unmount } = renderHook(() => useMarketHub(['BTCUSDT']))
    await waitFor(() => {
      expect(result.current.status).toBe('connected')
    })

    unmount()

    expect(latestConnection().stop).toHaveBeenCalledOnce()
  })
})
