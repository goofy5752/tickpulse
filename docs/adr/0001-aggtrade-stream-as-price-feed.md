# 0001. The `aggTrade` stream is the price feed

- **Status:** Accepted
- **Date:** 2026-11-03

## Context

TickPulse computes a 24-hour average and simple moving averages from "price updates" it collects
from Binance spot. Binance offers several WebSocket streams that could be read as a price update:

- `<symbol>@trade`: every individual trade.
- `<symbol>@aggTrade`: trades aggregated per taker order and price, with a monotonically increasing
  aggregate id per symbol.
- `<symbol>@miniTicker` and `<symbol>@avgPrice`: pre-aggregated snapshots, pushed about once per second.
- `<symbol>@kline_1m`: the current 1-minute candle, pushed while it changes.

The choice decides what the 24h average means, how duplicates are detected, and whether missed data
can be recovered after a disconnect.

## Decision

Subscribe to `<symbol>@aggTrade` on one combined-stream connection
(`wss://stream.binance.com:9443/stream`, configurable as `Binance:WsBaseUrl`). Every aggregate trade
is one tick: `(symbol, agg_trade_id, price, quantity, trade_time)`, with prices and quantities parsed
from their JSON strings straight into `decimal`.

## Alternatives

- **`@trade`.** The same prices, more messages, and no gain for an average of executed prices. Binance's
  REST endpoint for recovering a range by id is `aggTrades`, so `@trade` would also have no gap-fill.
- **`@miniTicker` / `@avgPrice`.** Rejected: these are server-side snapshots on a timer, not price
  updates. An average over them is an average of samples Binance chose, not of trading.
- **`@kline_1m`.** Rejected: it loses per-trade granularity. The 24h average would become an average
  of candle closes.

## Consequences

- The aggregate id is a total order per symbol. The tick primary key
  `(symbol_id, agg_trade_id, trade_time)` makes replays, reconnect overlaps and rotation overlaps
  idempotent (`ON CONFLICT DO NOTHING`).
- Missed trades can be fetched by id with REST `GET /api/v3/aggTrades?fromId=…`, which is what the
  gap-fill does after a reconnect, a restart or a leadership handover
  ([0022](0022-gap-fill-watermarks-seeded-from-the-database.md)).
- Candles merge by trade id, not by trade time: `first_trade_id` and `last_trade_id` decide open and
  close, so two trades with the same millisecond still have a deterministic order.
- Message volume is moderate. A local stack with the three seeded symbols reported about 22 ticks/s
  in `/api/system/stats` (2026-11-04, a quiet morning).
