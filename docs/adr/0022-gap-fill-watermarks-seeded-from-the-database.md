# 0022. Gap-fill watermarks are seeded from the database at every leadership session

- **Status:** Accepted
- **Date:** 2026-11-04

## Context

After a reconnect, the ingestion worker recovers missed trades over REST with
`aggTrades?fromId=<last stored id + 1>`, up to the first trade the new connection streamed. The "last
id" per symbol, the watermark, lived only in memory (`GapFillWatermarks`). That covered reconnects
inside one process, but not:

- an API **restart or redeploy**: a new process starts with no watermarks;
- a **leadership handover**: the new leader never saw the old leader's ids.

The end-to-end run on the local stack showed it: every `docker restart` of the api left gaps in
`agg_trade_id` (13 BTC and 31 ETH trades in one restart), and the candles of the restart minute differed
from Binance.

## Decision

**Goal.** No Binance aggTrade id is skipped: every id is stored or still scheduled to be fetched.
Fetching an id twice is harmless (`ON CONFLICT DO NOTHING`; candles fold inserted rows only).

**Seed.** At the start of every leadership session, before subscribing, the worker reads
`max(agg_trade_id)` per active symbol over `price_ticks` with `trade_time > now − 1 day`: one backward
primary-key probe per symbol on recent, uncompressed chunks (0.377 ms in an `EXPLAIN ANALYZE`). A seeded
symbol is an open gap, so the gap-filler recovers `last + 1 …` up to the stream boundary. A failed seed
ends the session; the leadership loop retries it.

**State per symbol.** `LastId` is the end of the contiguous prefix: every id up to it is stored, in
flight, or stored by another instance. Alongside are a pending flag, a version that every change bumps
(so a fill that started earlier cannot close or raise past a change), and `Above`, the highest id any
ended connection delivered, or that a dropped batch reported missing. `Above` only grows.

**One rule.** `LastId` moves up only across ids that were delivered or are foreign. Each operation keeps
it:

- `Merge` (a connection ended): raises a closed entry only over a contiguous span. A stream that began
  above `LastId + 1` opens a gap instead.
- `Verify` (the live connection, every reconcile pass): opens such a gap early. It never raises.
- `Seed`: raises `LastId` to the stored maximum only when `Above <= LastId`. Then this process delivered
  nothing above it, so what the database holds there came from another instance. Otherwise the fill
  starts at `LastId + 1` and walks up through stored ids, which is slower but never lossy.
- `Filled` and `Advance`: raise `LastId` to the last id the fill emitted, only if the version still
  matches.

A short or empty REST page ends a fill only when it reaches the stream boundary, so a lagging REST
answer cannot close a gap below ids the stream already delivered. Open gaps are re-filled on every
reconcile pass, one run per symbol at a time.

**Lead-in.** A symbol streamed for the first time in a session with nothing stored gets a lead-in: REST
finds the first trade at or after the start of the current minute (minus 5 s for clock skew), and the
gap-fill completes that minute, so the first candle is not partial. A failed lookup is retried on the
next connection.

**Tail-out.** The mirror of the lead-in, for a symbol that is removed: its UNSUBSCRIBE takes effect
mid-minute, so that minute would keep only the trades streamed before it, with a wrong close. Once the
minute is over (plus 5 s), one bounded REST pass fetches the trades from the last id the connection
delivered up to the end of that minute, through the gap-fill path. It runs in the background and never
delays the unsubscribe, and it moves no watermark (the removed symbol's entry is dropped). It stores nothing
past that minute, so a later reactivation's backfill of the inactive stretch still starts right after it.
Best-effort: a failure or the end of the session leaves the minute partial, as before.

## Alternatives

- **Persisting the watermarks in a table.** Rejected: the ticks themselves already are the record of
  what is stored; a second table could disagree with them.
- **Accepting the loss across restarts.** Rejected: zero data loss is a goal, and redeploys are routine.
- **Rebuilding the restart minute from a kline.** Rejected: the candle would claim trades that
  `price_ticks` does not have, so the 24h average and the candle would disagree.
- **Flags for each way a gap can become unsafe** (the first version of this change). Replaced by the single
  rule above after repeated reviews kept finding paths the flags did not cover.

## Consequences

- Restarts, redeploys and handovers recover every trade inside the 1-day seed window
  (`IngestionWorkerTests.Restart_GapFillsTradesMissedWhileDown_ExactlyOnce`,
  `HandoverAndBack_FillsFromTheOtherLeadersLastStoredTrade`). A live check over repeated restarts found
  no gaps and no duplicates.
- Recovery after a conservative seed may be slower: a fill walks through stored ids, one page per 1,000,
  capped at 50 pages per connection and re-armed every pass.
- **Known limitation:** a hole below the stored maximum that only another, or a crashed, process knew
  about is invisible to the seed. Tracked as
  [issue #19](https://github.com/goofy5752/tickpulse/issues/19).
- The gap-fill trade counter counts trades handed to the writer, including overlap that the primary key
  skips.

The defect history is in [F2](../ai/defects-log.md#f2).
