/*
 * Development-only stand-in for `@microsoft/signalr`, aliased in by vite.config.ts only while
 * `vite serve` runs with VITE_MOCK=1. It implements the slice of the client that useMarketHub
 * uses and streams the market simulation's prices and stats like the real `/hubs/market`.
 */
import { market } from './market'

export const HubConnectionState = {
  Disconnected: 'Disconnected',
  Connecting: 'Connecting',
  Connected: 'Connected',
  Disconnecting: 'Disconnecting',
  Reconnecting: 'Reconnecting',
} as const

export const LogLevel = { Trace: 0, Debug: 1, Information: 2, Warning: 3, Error: 4, Critical: 5, None: 6 } as const

type Handler = (payload: never) => void
const STATS_INTERVAL_MS = 2000

class MockHubConnection {
  state: string = HubConnectionState.Disconnected
  private readonly handlers = new Map<string, Handler[]>()
  private readonly groups = new Set<string>()
  private statsTimer: ReturnType<typeof setInterval> | undefined
  private unsubscribe: (() => void) | undefined

  on(name: string, handler: Handler) {
    this.handlers.set(name, [...(this.handlers.get(name) ?? []), handler])
  }

  onreconnecting(_handler: () => void) {
    // The simulation never drops the connection.
  }

  onreconnected(_handler: () => void) {
    // The simulation never drops the connection.
  }

  onclose(_handler: () => void) {
    // The simulation never drops the connection.
  }

  private emit(name: string, payload: unknown) {
    for (const handler of this.handlers.get(name) ?? []) (handler as (value: unknown) => void)(payload)
  }

  async start() {
    this.state = HubConnectionState.Connecting
    await new Promise((resolve) => setTimeout(resolve, 350))
    this.state = HubConnectionState.Connected
    this.emit('stats', market.stats())
    this.statsTimer = setInterval(() => {
      this.emit('stats', market.stats())
    }, STATS_INTERVAL_MS)
    this.unsubscribe = market.onPrice((price) => {
      if (this.groups.has(price.symbol)) this.emit('price', price)
    })
  }

  stop() {
    clearInterval(this.statsTimer)
    this.unsubscribe?.()
    this.groups.clear()
    this.state = HubConnectionState.Disconnected
    return Promise.resolve()
  }

  invoke(method: string, symbol: string) {
    if (method === 'Subscribe') this.groups.add(symbol.toUpperCase())
    if (method === 'Unsubscribe') this.groups.delete(symbol.toUpperCase())
    return Promise.resolve()
  }
}

export class HubConnectionBuilder {
  withUrl(_url: string) {
    return this
  }

  withAutomaticReconnect(_policy?: unknown) {
    return this
  }

  configureLogging(_level: unknown) {
    return this
  }

  build() {
    return new MockHubConnection()
  }
}
