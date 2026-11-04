# 0022. Gap-fill watermarks are seeded from the database at every leadership session

- **Status:** Accepted. The implementation is under review as of 2026-11-04 (defects F2 and F6 in the
  [defects log](../ai/defects-log.md)).
- **Date:** 2026-11-04

## Context

After a reconnect, the ingestion worker recovers missed trades over REST with
`aggTrades?fromId=<last stored id + 1>`, up to the first trade the new connection streamed. The "last
id" per symbol, the watermark, lived only in memory (`GapFillWatermarks`). That covered reconnects
inside one process, but not:

- an API **restart or redeploy**: a new process starts with no watermarks;
- a **leadership handover**: the new leader never saw the old leader's ids.

The end-to-end run on the local stack showed it: every `docker restart` of the api left gaps in `agg_trade_id` (13 BTC and
31 ETH trades in one restart), and the candles of the restart minute differed from Binance.

## Decision

- At the start of every leadership session, before subscribing, the worker **seeds** the watermarks
  from the database: `max(agg_trade_id)` per active symbol over `price_ticks` with
  `trade_time > now − 1 day`. The query is one backward primary-key probe per symbol on recent,
  uncompressed chunks (0.377 ms in an `EXPLAIN ANALYZE` on the local stack).
- A seeded entry is an open gap, so the existing gap-filler recovers `last + 1 …` up to the stream
  boundary, with its page cap and idempotent overlap.
- In-memory knowledge wins over the seed. An open gap below data this process has already stored is a
  **hole** and is never raised by a seed, so a stale or partial fill can never skip ids.
- A symbol streamed for the first time in a session, with nothing stored, gets a **lead-in**: REST
  finds the first trade at or after the start of the current minute (minus 5 s for clock skew), and the
  gap-fill completes that minute, so the first candle is not partial.
- A failed seed ends the session; the leadership loop retries it. It never streams on with an
  unnoticed gap.

## Alternatives

- **Persisting the watermarks in a table.** Rejected: the ticks themselves already are the record of
  what is stored; a second table could disagree with them.
- **Accepting the loss across restarts.** Rejected: zero data loss is a goal, and redeploys are routine.
- **Rebuilding the restart minute from a kline.** Rejected: the candle would claim trades that
  `price_ticks` does not have, so the 24h average and the candle would disagree.

## Consequences

- Restarts, redeploys and handovers recover every trade inside the 1-day seed window
  (`IngestionWorkerTests.Restart_GapFillsTradesMissedWhileDown_ExactlyOnce`,
  `HandoverAndBack_FillsFromTheOtherLeadersLastStoredTrade`).
- Holes make recovery conservative: a fill starts at the hole and walks forward, capped at 50 pages
  per connection. Recovery may be slower; it is never lossy.
- Known limit: a hole below the stored maximum that only another, or a crashed, process knew about is
  invisible to the seed.
- As of 2026-11-04, reviews of this change have found three Critical loss paths in it before merge:
  - a seed raising a watermark past an open gap, addressed by the hole rule above;
  - trades stored by a discarded connection being invisible to the seed, addressed by a per-symbol
    "stored above" record;
  - a completed fill clearing that record while the live link was stalled, being fixed in the current
    round.

  The details are in defect [F2](../ai/defects-log.md#f2).
