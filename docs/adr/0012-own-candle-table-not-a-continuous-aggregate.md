# 0012. `candles_1m` is our own table, not a continuous aggregate

- **Status:** Accepted
- **Date:** 2026-11-03

## Context

TimescaleDB offers continuous aggregates, which would maintain 1-minute candles from `price_ticks`
automatically. TickPulse needs a 1-minute candle table that:

- the **backfill writes into directly**, from Binance klines, for minutes that have no ticks at all
  ([0007](0007-switchable-kline-backfill-for-sma-only.md));
- carries the exact `price_sum` and `tick_count` of each stream minute, so the 24h average can be
  computed from candles plus two edge minutes of raw ticks
  ([0002](0002-24h-average-is-the-arithmetic-mean.md));
- outlives the 14-day retention of raw ticks.

There is also a licensing constraint. TimescaleDB ships two editions. Hypertables and `time_bucket`
are Apache-2 licensed. Compression, retention policies and continuous aggregates are under the
Timescale License (TSL, "community"). The `timescale/timescaledb` image TickPulse runs includes the
TSL features, but managed offerings that ship only the Apache-2 edition do not. Azure Database for
PostgreSQL is one of them ([deployment.md](../deployment.md#managed-postgresql)).

## Decision

- `candles_1m (symbol_id, bucket_start, open, high, low, close, first_trade_id, last_trade_id,
  price_sum, tick_count, volume, quote_volume, source)` is a plain table with primary key
  `(symbol_id, bucket_start)`, kept forever.
- The tick writer upserts the affected minutes in the same transaction as the ticks it inserted:
  open from the lowest trade id, close from the highest, high and low widened, sums and counts added.
  Ticks that were already stored (`ON CONFLICT DO NOTHING`) never reach the candle step, so replays
  are not counted twice.
- **The query logic depends on no TSL feature.** Compression (after 2 days) and retention (14 days) on
  `price_ticks` are policies only. Without them the data grows, but every query answers the same.

## Alternatives

- **A continuous aggregate over `price_ticks`.** Rejected, for four reasons:
  - The backfill cannot write into it.
  - Materialised buckets survive the 14-day retention of their ticks only as long as no refresh covers
    that range again. A refresh window wide enough to pick up late or gap-filled ticks would therefore
    have to be kept clear of the retention horizon.
  - Already materialised buckets go stale until a refresh runs.
  - It is a TSL feature.
- **Candles computed on read from raw ticks.** Rejected: too slow for 1d and 1w, and impossible beyond
  the 14-day retention.

## Consequences

- One write path merges stream batches, gap-fill pages and rotation overlaps exactly; integration
  tests compare it with a recomputation from raw ticks.
- On an Apache-only PostgreSQL, the migration's `ALTER TABLE … SET (timescaledb.compress …)`,
  `add_compression_policy` and `add_retention_policy` steps fail and must be made conditional, with
  retention replaced by a scheduled `drop_chunks` (Apache-2). The scale-out design covers this.
- Higher periods are kept in rollups maintained the same way, by our own code rather than by TSL
  features ([0015](0015-candle-rollups-maintained-by-triggers.md)).
