# 0007. A switchable kline backfill feeds long-period SMAs, never the 24h average

- **Status:** Accepted
- **Date:** 2026-11-03

## Context

A 20-week SMA needs 20 weeks of daily closes. With collected data only, `p=1w` would answer
"1 of 20 points" for months after a symbol is added, and `p=1d` for weeks. The 24h average, on the
other hand, is defined as an average over *collected* trades
([0002](0002-24h-average-is-the-arithmetic-mean.md)) and must not change meaning.

## Decision

- When a symbol is added, and on first start for the seeded symbols, a background worker fetches
  **1-minute klines** from Binance REST (`GET /api/v3/klines`, 1000 per page) into `candles_1m`, marked
  `source = 2` (backfill).
- It walks oldest first from `now − Backfill:Depth` (default `182.00:00:00`, 26 weeks) up to the first
  streamed minute. A run interrupted by a restart or a lost leadership resumes from what is stored.
- Backfill rows carry no `price_sum`, `tick_count` or trade ids. The 24h average reads only
  `source = 1` (stream) minutes and raw ticks, so backfill can never reach it.
- A stream minute replaces a backfill minute for the same bucket. A backfill insert never overwrites a
  stream minute (`ON CONFLICT DO NOTHING`).
- `Backfill:Enabled` is **off** in `appsettings.json`, so the bare service is collected-data only.
  It is **on** in `docker-compose.yml` and in production.
- The status is visible per symbol (`backfillStatus`: `NotRequested`, `Pending`, `Running`,
  `Completed`, `Failed`, plus `backfilledFrom`) in `/api/symbols`, the admin API, the CLI and the
  dashboard.

## Alternatives

- **No backfill.** Rejected: the long periods would be close to useless for weeks.
- **Backfilling raw trades** with `aggTrades`. Rejected: hundreds of times more requests and rows for
  data that only the SMA needs at minute resolution.

## Consequences

- The live run fetched 262,080 klines per symbol in about 2 minutes (263 pages), well inside Binance's
  request-weight limits. Symbols backfill one after another, so a fresh stack with 3 symbols needs a
  few minutes before every long-period SMA is complete.
- The REST client honours Binance's weight headers, waits out `429` and treats `418` as a ban
  (`BinanceWeightGuard`). A ban marks only the current symbol `Failed` and requeues it after the wait.
- The backfill runs only on the leader ([0011](0011-single-process-with-a-leader-lock.md)).
- Until a symbol's backfill completes, its 24h change on `/api/symbols` is `null` when the day-ago
  close is more than 15 minutes older than `now − 24 h` (defect F3 in the
  [defects log](../ai/defects-log.md)).
