# 0022. Gap-fill watermarks are seeded from the database at every leadership session

- **Status:** Accepted
- **Date:** 2026-11-04 (revised the same day: seed window, rotation handover, longer outages)

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

**Seed.** At the start of every leadership session, before subscribing, the worker reads, per active
symbol, the `agg_trade_id` of the newest trade in `price_ticks` with `trade_time > now − 12 days`. REST
`fromId` has no time limit, so an outage of days is recovered trade by trade. The window is the tick
writer's replay window (13 days) less one day of headroom, so every trade the fill recovers is still
insertable when it gets there. A fill reads up to 50,000 trades per reconcile pass, so even the busiest
symbol's 12 days fit in that day.

The lookup is `ORDER BY trade_time DESC LIMIT 1`: an ordered ChunkAppend reads the chunks newest first
and stops at the first row. That is one index probe on `(symbol_id, trade_time DESC)`, or the
trade-time metadata of the newest compressed batch, in the newest chunk holding a trade of the symbol.
Older chunks are never read. Over a 12-day window most chunks are compressed (the policy compresses
after 2 days, and keeps compressing while the API is down). The lookup runs with `SET LOCAL
enable_sort = off`. Without it the planner sometimes picks a top-N sort over every chunk, which
decompresses all of them: the integration tests saw this with a handful of rows, and stale statistics
after an outage could cause it with real volumes. Exchange ids grow with trade time, so the
newest trade has the highest id. Where ids are out of order, the result is lower, which only makes the
fill read a few stored trades again.

A seeded symbol is an open gap, so the gap-filler recovers `last + 1 …` up to the stream boundary. A
failed seed ends the session; the leadership loop retries it.

**State per symbol.** `LastId` is the end of the contiguous prefix: every id up to it is stored, in
flight, or stored by another instance. Alongside are a pending flag, a version that every change bumps
(so a fill that started earlier cannot close or raise past a change), and `Above`, the highest id any
ended connection delivered, or that a dropped batch reported missing. `Above` only grows.

**One rule.** `LastId` moves up only across ids that were delivered or are foreign. Each operation keeps
it:

- `Merge` (a connection ended): raises a closed entry only over a contiguous span. A stream that began
  above `LastId + 1` opens a gap instead.
- `Verify` (the live connection, every reconcile pass): opens such a gap early. It never raises.
- `Seed`: raises `LastId` to the stored id only when `Above <= LastId`. Then this process delivered
  nothing above it, so what the database holds there came from another instance. Otherwise the fill
  starts at `LastId + 1` and walks up through stored ids, which is slower but never lossy.
- `Filled` and `Advance`: raise `LastId` to the last id the fill emitted, only if the version still
  matches.

A short or empty REST page ends a fill only when it reaches the stream boundary, so a lagging REST
answer cannot close a gap below ids the stream already delivered. Open gaps are re-filled on every
reconcile pass, one run per symbol at a time.

**Rotation handover.** A clean rotation hands the retired link's last id per symbol to the new link. It
also passes on whatever the retired link had taken over itself. The new link was subscribed before the
old one closed, so it carries every trade after those ids. The handover happens after the old pump has
drained and before its retirement completes, and fills on the new link wait for that retirement. Until
the new link delivers a symbol, a short REST page completes the fill only once it has reached past the
handed-over id. Before this, such a fill had no boundary, so lagging REST closed the gap below ids that
only the retired link had delivered
([issue #19](https://github.com/goofy5752/tickpulse/issues/19)). The handed-over id never stops what is
emitted, so a predecessor that stalled still gets its missing trades fetched, and `Verify` opens that
gap as before.

**Outage longer than the seed window.** The seed finds no trade, so the symbol only gets a lead-in for
its first minute. Trades older than the replay window cannot be inserted anyway. The candles of that
stretch come from klines instead, when backfill is enabled. Every leadership session queues every
active symbol in the backfill worker. That includes completed symbols and sessions the worker never saw
begin, which `IngestionState.LeaderSinceUtc` reveals. A symbol whose last streamed minute before the
session is older than the seed window gets the minutes from right after the latest minute stored before
the session's first streamed minute, up to that minute. Until the session has streamed the symbol, the
fill goes up to the session start, and the symbol is checked again on every poll. A shorter outage is
left to the trade gap-fill, which rebuilds the exact candles.

**Lead-in.** A symbol streamed for the first time in a session with nothing stored gets a lead-in: REST
finds the first trade at or after the start of the current minute (minus 5 s for clock skew), and the
gap-fill completes that minute, so the first candle is not partial. A failed lookup is retried on the
next connection.

## Alternatives

- **Persisting the watermarks in a table.** Rejected: the ticks themselves already are the record of
  what is stored; a second table could disagree with them.
- **Accepting the loss across restarts.** Rejected: zero data loss is a goal, and redeploys are routine.
- **Rebuilding the restart minute from a kline.** Rejected: the candle would claim trades that
  `price_ticks` does not have, so the 24h average and the candle would disagree. Klines fill only the
  stretch that no trade can fill any more (an outage longer than the seed window). They are stored as
  backfill rows, without sums, and never feed the 24h average.
- **Flags for each way a gap can become unsafe** (the first version of this change). Replaced by the single
  rule above after repeated reviews kept finding paths the flags did not cover.
- **`max(agg_trade_id)` over the seed window** (the first version of the seed). Rejected once the window
  grew past the uncompressed chunks. The planner merges one row from every chunk in id order. On a
  compressed chunk that means decompressing and sorting all of the symbol's rows there. With 3.2 million
  ticks over 13 days, 11 of them compressed, that took 279 ms against 0.17 ms for the newest-trade
  lookup, and the cost grows with trading volume.
- **A seed window equal to the replay window.** Rejected: the first trades of a fill for an outage close
  to 13 days would fall out of the replay window while the fill is still running, and could no longer
  be inserted or reached by klines.

## Consequences

- Restarts, redeploys and handovers recover every trade inside the 12-day seed window, including
  after an outage of days, when the last stored trades lie in compressed chunks
  (`IngestionWorkerTests.Restart_GapFillsTradesMissedWhileDown_ExactlyOnce`,
  `RestartAfterAnOutageOfDays_GapFillsFromTheLastStoredTrade`,
  `HandoverAndBack_FillsFromTheOtherLeadersLastStoredTrade`; the plan in
  `MarketDataQueriesTests.LatestTradeIdsQuery_ReadsOnlyTheNewestChunkWithTrades_EvenWhenOlderOnesAreCompressed`).
  A live check over repeated restarts found no gaps and no duplicates.
- A replay into compressed chunks inserts nothing and leaves candles and rollups unchanged
  (`TickWriterTests.WriteBatch_ReplayIntoACompressedChunk_InsertsNothingAndLeavesCandlesAndRollups`).
- After a longer outage, the trades of the stretch are not stored. With backfill enabled, its candles
  come from klines (`BackfillWorkerTests.OutageLongerThanTheSeedWindow_*`,
  `NewLeadershipSession_EvenOneTheWorkerNeverSawStart_ChecksCompletedSymbolsAgain`). With backfill
  disabled, the candles have a hole there.
- A rotation with REST lagging and a new link that has not streamed a symbol yet keeps the gap open
  until REST has reached past the retired link's last id
  (`IngestionWorkerTests.Rotation_NewLinkSilent_RestBehind_GapStaysOpenUntilTheRetiredLinksLastId`).
- Recovery after a conservative seed may be slower: a fill walks through stored ids, one page per 1,000,
  capped at 50 pages per connection and re-armed every pass.
- **Known limitation:** a hole below the newest stored trade that only another, or a crashed, process
  knew about is invisible to the seed.
- The gap-fill trade counter counts trades handed to the writer, including overlap that the primary key
  skips.

The defect history is in [F2](../ai/defects-log.md#f2).
