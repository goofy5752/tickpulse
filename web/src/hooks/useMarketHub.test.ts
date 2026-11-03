import type * as SignalR from '@microsoft/signalr'
import { HubConnectionState } from '@microsoft/signalr'
import { act, renderHook, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { LivePrice, SystemStatsResponse } from '@/api/types'
import { useMarketHub } from './useMarketHub'

type Handler = (...args: unknown[]) => void

/** Stands in for a SignalR HubConnection; tests drive its lifecycle callbacks by hand. */
class FakeHubConnection {
  state: HubConnectionState = HubConnectionState.Disconnected
  private readonly handlers = new Map<string, Handler>()
  private reconnectingCallback: ((error?: Error) => void) | undefined
  private reconnectedCallback: ((connectionId?: string) => void) | undefined
  private closeCallback: ((error?: Error) => void) | undefined
  private stopRequested = false

  // Like the real client: Connecting while the handshake runs, Connected once start() resolves,
  // and a stop() during the handshake makes start() reject.
  start = vi.fn(async () => {
    this.stopRequested = false
    this.state = HubConnectionState.Connecting
    await Promise.resolve()
    if (this.wasStopped()) {
      this.state = HubConnectionState.Disconnected
      throw new Error('The connection was stopped during negotiation.')
    }
    this.state = HubConnectionState.Connected
  })

  // Like the real client: stopping a started connection fires onclose.
  stop = vi.fn(() => {
    const wasStarted = this.state === HubConnectionState.Connected || this.state === HubConnectionState.Reconnecting
    this.stopRequested = true
    this.state = HubConnectionState.Disconnected
    if (wasStarted) this.closeCallback?.()
    return Promise.resolve()
  })

  invoke = vi.fn((_method: string, ..._args: unknown[]) => Promise.resolve(undefined))

  // A method so the check after an await reads the current value.
  private wasStopped(): boolean {
    return this.stopRequested
  }

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

  onclose(callback: (error?: Error) => void) {
    this.closeCallback = callback
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

  /** The connection ends for good without the hook asking (e.g. the server closed it). */
  simulateClose(error: Error) {
    this.state = HubConnectionState.Disconnected
    this.closeCallback?.(error)
  }

  invokedWith(method: string): unknown[] {
    return this.invoke.mock.calls.filter(([m]) => m === method).map(([, symbol]) => symbol)
  }
}

/** Lets a test configure the next connection the hook builds (e.g. make start() fail). */
let configureNextConnection: ((connection: FakeHubConnection) => void) | undefined

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
      configureNextConnection?.(connection)
      configureNextConnection = undefined
      hub.connections.push(connection)
      return connection
    }
  }
  return { ...actual, HubConnectionBuilder }
})

function builtConnections(): FakeHubConnection[] {
  return hub.connections.map((connection) => {
    if (!(connection instanceof FakeHubConnection)) throw new Error('Unexpected hub connection type')
    return connection
  })
}

function latestConnection(): FakeHubConnection {
  const connection = builtConnections().at(-1)
  if (!connection) throw new Error('No hub connection was built')
  return connection
}

/** Runs due timers and settles the promises they start, inside act so React applies the updates. */
async function advance(ms: number) {
  await act(async () => {
    await vi.advanceTimersByTimeAsync(ms)
  })
}

beforeEach(() => {
  hub.connections.length = 0
  hub.urls.length = 0
  configureNextConnection = undefined
})

afterEach(() => {
  vi.useRealTimers()
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

  it('useMarketHub_RemovedSymbol_DropsItsPriceSoReAddingDoesNotShowAStaleOne', async () => {
    const { result, rerender } = renderHook(({ symbols }) => useMarketHub(symbols), {
      initialProps: { symbols: ['BTCUSDT', 'ETHUSDT'] },
    })
    await waitFor(() => {
      expect(result.current.status).toBe('connected')
    })
    act(() => {
      latestConnection().emit('price', { symbol: 'ETHUSDT', price: 3611.08, tradeTime: '2026-11-03T12:00:00Z' })
    })

    rerender({ symbols: ['BTCUSDT'] })
    rerender({ symbols: ['BTCUSDT', 'ETHUSDT'] })

    expect(result.current.prices.ETHUSDT).toBeUndefined()
  })

  it('useMarketHub_StrictModeDoubleMount_OnlySecondConnectionSubscribes', async () => {
    const { result } = renderHook(() => useMarketHub(['BTCUSDT']), { reactStrictMode: true })

    await waitFor(() => {
      expect(result.current.status).toBe('connected')
    })
    const connections = builtConnections()
    expect(connections).toHaveLength(2)
    const [first, second] = connections as [FakeHubConnection, FakeHubConnection]
    expect(first.stop).toHaveBeenCalledOnce()
    expect(first.invokedWith('Subscribe')).toEqual([])
    expect(second.stop).not.toHaveBeenCalled()
    await waitFor(() => {
      expect(second.invokedWith('Subscribe')).toEqual(['BTCUSDT'])
    })
  })

  it('useMarketHub_InitialStartFails_RetriesAfterOneSecond_AndStopsRetryingOnUnmount', async () => {
    vi.useFakeTimers()
    configureNextConnection = (connection) => {
      connection.start
        .mockRejectedValueOnce(new Error('negotiation failed'))
        .mockRejectedValueOnce(new Error('negotiation failed'))
    }

    const { result, unmount } = renderHook(() => useMarketHub(['BTCUSDT']))
    const connection = latestConnection()
    await advance(0)

    expect(connection.start).toHaveBeenCalledTimes(1)
    expect(result.current.status).toBe('disconnected')

    await advance(999)
    expect(connection.start).toHaveBeenCalledTimes(1)
    await advance(1)
    expect(connection.start).toHaveBeenCalledTimes(2)

    // The second attempt failed too and scheduled a third; unmounting must cancel it.
    expect(vi.getTimerCount()).toBe(1)
    unmount()
    expect(vi.getTimerCount()).toBe(0)
    await advance(60_000)
    expect(connection.start).toHaveBeenCalledTimes(2)
  })

  it('useMarketHub_InitialStartFailsOnce_ConnectsAndSubscribesOnRetry', async () => {
    vi.useFakeTimers()
    configureNextConnection = (connection) => {
      connection.start.mockRejectedValueOnce(new Error('negotiation failed'))
    }

    const { result } = renderHook(() => useMarketHub(['BTCUSDT']))
    await advance(0)
    expect(result.current.status).toBe('disconnected')

    await advance(1_000)

    expect(result.current.status).toBe('connected')
    expect(latestConnection().invokedWith('Subscribe')).toEqual(['BTCUSDT'])
  })

  it('useMarketHub_ConnectionCloses_GoesDisconnectedThenRestartsAndResubscribes', async () => {
    vi.useFakeTimers()
    const { result } = renderHook(() => useMarketHub(['BTCUSDT']))
    await advance(0)
    expect(result.current.status).toBe('connected')
    const connection = latestConnection()
    connection.invoke.mockClear()

    act(() => {
      connection.simulateClose(new Error('server closed the connection'))
    })

    expect(result.current.status).toBe('disconnected')
    expect(connection.start).toHaveBeenCalledTimes(1)

    await advance(1_000)

    expect(connection.start).toHaveBeenCalledTimes(2)
    expect(result.current.status).toBe('connected')
    expect(connection.invokedWith('Subscribe')).toEqual(['BTCUSDT'])
    expect(builtConnections()).toHaveLength(1)
  })
})
