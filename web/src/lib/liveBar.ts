/** One OHLC bucket; `time` is the bucket start in UNIX seconds (UTC). */
export interface OhlcBar {
  time: number
  open: number
  high: number
  low: number
  close: number
}

export interface Trade {
  price: number
  tradeTime: string
}

/**
 * Folds one live trade into the newest bar. A trade inside the bar's bucket widens high/low and
 * sets the close, so a bucket's range accumulates across every trade until the next refetch. A
 * trade past the bucket opens a new bar that starts from the trade itself. Older or unparseable
 * trades leave the bar untouched (same reference).
 */
export function mergeTrade(bar: OhlcBar | undefined, trade: Trade, periodSeconds: number): OhlcBar | undefined {
  if (!bar) return undefined
  const t = Math.floor(Date.parse(trade.tradeTime) / 1000)
  if (!Number.isFinite(t) || t < bar.time) return bar
  if (t < bar.time + periodSeconds) {
    return {
      ...bar,
      high: Math.max(bar.high, trade.price),
      low: Math.min(bar.low, trade.price),
      close: trade.price,
    }
  }
  const time = bar.time + Math.floor((t - bar.time) / periodSeconds) * periodSeconds
  return { time, open: trade.price, high: trade.price, low: trade.price, close: trade.price }
}

/**
 * Combines the locally accumulated bar with a freshly fetched newest candle. The server wins for
 * the close, but a range the live trades already saw is kept; a bucket the trades opened that the
 * server does not list yet is kept as it is.
 */
export function reconcileBar(local: OhlcBar | undefined, server: OhlcBar | undefined): OhlcBar | undefined {
  if (!server) return undefined
  if (!local || local.time < server.time) return server
  if (local.time > server.time) return local
  return { ...server, high: Math.max(server.high, local.high), low: Math.min(server.low, local.low) }
}
