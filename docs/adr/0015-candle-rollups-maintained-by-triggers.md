# 0015. Hourly and daily candle rollups, maintained by triggers

- **Status:** Accepted
- **Date:** 2026-11-04 (PR #12)

## Context

1d and 1w analytics originally aggregated `candles_1m` with `time_bucket` and `first/last` on every
request: about 80,000 minute rows for an 8-week SMA, and about 262,000 for 300 daily candles. In the
live load test (50 connections, 30 s), SMA `1w n=8` had a p95 of 83 ms against a 50 ms target, and a
single `candles?p=1d` call took about 135 ms. Database CPU reached 2,100 %. Closed buckets almost
never change, but they were recomputed every time.

## Decision

- Two plain tables, `candles_1h` and `candles_1d` (OHLCV, primary key `(symbol_id, bucket_start)`),
  added by migration `20261104090000_CandleRollups`, which also fills them from existing minutes.
- Statement-level `AFTER INSERT / UPDATE / DELETE` triggers on `candles_1m`, with transition tables,
  keep them exact **inside the writing transaction**. The affected hours are *recomputed* from
  `candles_1m`, then the affected days from `candles_1h`.
- 1d and 1w closes (for day-aligned windows) and 1d/1w candles read `candles_1d`. Shorter periods
  and unaligned windows still read minutes.
- Concurrent writers first lock the affected `candles_1d` rows in key order, then recompute in a
  fresh READ COMMITTED snapshot, so neither loses the other's minutes.

## Alternatives

- **Merge deltas (GREATEST/LEAST) instead of recomputing.** Rejected: a stream minute that replaces a
  backfill minute can *lower* a day's high or *raise* its low, which a merge cannot express.
- **Maintenance code in the two writers.** Rejected: triggers also cover an older image running after
  an automatic rollback, and any manual repair.
- **Continuous aggregates.** Rejected for the same reasons as
  [0012](0012-own-candle-table-not-a-continuous-aggregate.md): stale materialised buckets after
  backfill and gap-fill, a hypertable rewrite, and a TSL dependency.
- **Completed periods from a rollup, the current one from minutes.** Rejected: completed periods still
  change (backfill, gap-fill), so they need maintenance on write anyway.

## Consequences

- Same data, same load: SMA `1d n=20` p95 went from 65.4 to 19.8 ms, SMA `1w n=8` from 184.8 to
  20.1 ms, candles `1d limit=300` from 1,368 to 20.5 ms, and single 1d/1w candle calls from about
  150 ms to about 4 ms. Results are byte-identical to the old SQL.
- Deadlock freedom rests on an invariant the writers follow, documented in the migration: one
  `candles_1m` statement per transaction, minute locks before day locks, day locks in key order.
- `CandleRollupTests` guards it: a randomised equivalence test against the original SQL as oracle,
  a deterministic overlapping-transaction test (which fails without the day lock), a concurrent
  writer test, the backfill-replacement case, deletes, routing, query plans and the migration
  backfill.
