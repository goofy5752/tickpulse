import {
  type HubConnection,
  HubConnectionBuilder,
  HubConnectionState,
  type IRetryPolicy,
  LogLevel,
} from '@microsoft/signalr'
import { useEffect, useMemo, useRef, useState } from 'react'
import { apiUrl } from '@/api/client'
import type { LivePrice, SystemStatsResponse } from '@/api/types'

export const MARKET_HUB_PATH = '/hubs/market'

/** Connection state of the browser's own link to the API's market hub. */
export type HubStatus = 'connecting' | 'connected' | 'reconnecting' | 'disconnected'

export interface MarketHubState {
  status: HubStatus
  /** Latest trade per subscribed symbol, keyed by the upper-case symbol. */
  prices: Record<string, LivePrice>
  /** The most recent `stats` push, or null until the first one arrives. */
  stats: SystemStatsResponse | null
}

// The dashboard is meant to stay open, so it never stops trying: quick first retries, then a
// steady 30 s cadence. The same schedule covers the very first start, which SignalR's automatic
// reconnect does not.
const RETRY_DELAYS_MS = [0, 1_000, 2_000, 5_000, 10_000, 30_000] as const

function retryDelay(attempt: number): number {
  return RETRY_DELAYS_MS[Math.min(attempt, RETRY_DELAYS_MS.length - 1)] ?? 30_000
}

const retryForever: IRetryPolicy = {
  nextRetryDelayInMilliseconds: ({ previousRetryCount }) => retryDelay(previousRetryCount),
}

function normalise(symbols: readonly string[]): string[] {
  return [...new Set(symbols.map((s) => s.trim().toUpperCase()).filter((s) => s.length > 0))].sort()
}

/**
 * Streams live prices for `symbols` and system stats from the market hub.
 *
 * One connection lives for the component's lifetime. Changing `symbols` joins and leaves hub
 * groups on that connection. After an automatic reconnect the server has forgotten the old
 * connection's groups, so every wanted symbol is subscribed again.
 */
export function useMarketHub(symbols: readonly string[]): MarketHubState {
  const symbolsKey = normalise(symbols).join(',')

  const [status, setStatus] = useState<HubStatus>('connecting')
  const [prices, setPrices] = useState<Record<string, LivePrice>>({})
  const [stats, setStats] = useState<SystemStatsResponse | null>(null)

  const connectionRef = useRef<HubConnection | null>(null)
  /** Symbols the caller wants. */
  const wantedRef = useRef(new Set<string>())
  /** Symbols joined on the current server-side connection. */
  const joinedRef = useRef(new Set<string>())

  // Owns the connection: build, start (retrying), and stop on unmount.
  useEffect(() => {
    const lifetime = new AbortController()
    // A function, not a flag: callbacks and awaits must see the value at the time they run.
    const disposed = () => lifetime.signal.aborted
    let retryTimer: ReturnType<typeof setTimeout> | undefined
    const joined = joinedRef.current

    const connection = new HubConnectionBuilder()
      .withUrl(apiUrl(MARKET_HUB_PATH))
      .withAutomaticReconnect(retryForever)
      .configureLogging(import.meta.env.DEV ? LogLevel.Warning : LogLevel.None)
      .build()
    connectionRef.current = connection

    connection.on('price', (update: LivePrice) => {
      const symbol = update.symbol.toUpperCase()
      if (!wantedRef.current.has(symbol)) return
      setPrices((previous) => ({ ...previous, [symbol]: { ...update, symbol } }))
    })
    connection.on('stats', (next: SystemStatsResponse) => {
      setStats(next)
    })

    connection.onreconnecting(() => {
      if (!disposed()) setStatus('reconnecting')
    })
    connection.onreconnected(() => {
      if (disposed()) return
      joined.clear()
      setStatus('connected')
      syncSubscriptions(connection, wantedRef.current, joined)
    })
    connection.onclose(() => {
      if (disposed()) return
      joined.clear()
      setStatus('disconnected')
      scheduleStart(1)
    })

    function scheduleStart(attempt: number) {
      retryTimer = setTimeout(() => void start(attempt), retryDelay(attempt))
    }

    async function start(attempt: number) {
      if (disposed()) return
      // Later attempts stay 'disconnected' until one succeeds, so the status does not flicker.
      if (attempt === 0) setStatus('connecting')
      try {
        await connection.start()
      } catch {
        if (disposed()) return
        setStatus('disconnected')
        scheduleStart(attempt + 1)
        return
      }
      if (disposed()) return
      setStatus('connected')
      syncSubscriptions(connection, wantedRef.current, joined)
    }

    void start(0)

    return () => {
      lifetime.abort()
      clearTimeout(retryTimer)
      connectionRef.current = null
      joined.clear()
      void connection.stop().catch(() => undefined)
    }
  }, [])

  // Follows the wanted symbols on whatever connection is currently up.
  useEffect(() => {
    wantedRef.current = new Set(symbolsKey.length > 0 ? symbolsKey.split(',') : [])
    const connection = connectionRef.current
    if (connection) syncSubscriptions(connection, wantedRef.current, joinedRef.current)
  }, [symbolsKey])

  // Prices for symbols that were dropped are hidden immediately, not when the next tick arrives.
  const visiblePrices = useMemo(() => {
    const wanted = new Set(symbolsKey.split(','))
    return Object.fromEntries(Object.entries(prices).filter(([symbol]) => wanted.has(symbol)))
  }, [prices, symbolsKey])

  return { status, prices: visiblePrices, stats }
}

/**
 * Joins the groups for wanted symbols that are not joined yet and leaves the ones no longer
 * wanted. `joined` is updated before each call returns, so overlapping syncs never double-join.
 */
function syncSubscriptions(connection: HubConnection, wanted: ReadonlySet<string>, joined: Set<string>) {
  if (connection.state !== HubConnectionState.Connected) return

  for (const symbol of wanted) {
    if (joined.has(symbol)) continue
    joined.add(symbol)
    connection.invoke('Subscribe', symbol).catch(() => {
      // Forget it so the next sync (symbol change or reconnect) tries again.
      joined.delete(symbol)
    })
  }
  for (const symbol of [...joined]) {
    if (wanted.has(symbol)) continue
    joined.delete(symbol)
    connection.invoke('Unsubscribe', symbol).catch(() => undefined)
  }
}
