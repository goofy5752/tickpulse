# 0006. Fewer than `n` buckets or gaps: use what exists, skip empty buckets

- **Status:** Accepted
- **Date:** 2026-11-03

## Context

A window of `n` buckets can hold fewer than `n` closes: the symbol was added recently, collection was
down for a while, or a thin pair had no trade in some minutes. The endpoint must still answer
honestly.

## Decision

- Average the closes of the **non-empty** buckets in the window. Empty buckets are skipped, never
  forward-filled.
- Report `requestedDataPoints` (`n`), `actualDataPoints` (the closes used) and the `dataPoints`
  themselves (`bucketStart`, `close`).
- When the window holds no close at all, answer `404` with the `no-data` problem.

## Alternatives

- **An error when fewer than `n` points exist.** Rejected: long periods would fail for days after a
  symbol is added, and one missing minute would fail a whole window.
- **Forward-filling empty buckets with the previous close.** Rejected: it invents data points, and an
  outage would look like a flat market.

## Consequences

- The response is self-describing: the dashboard shows "20 / 20 points" and a point strip, and a
  client can apply its own threshold.
- An outage reduces `actualDataPoints` but never corrupts the value.
- A symbol that was inactive for a while is backfilled over that stretch when it is reactivated
  ([0008](0008-removing-a-symbol-deactivates-it.md)), so with backfill on the gap does not persist.
