# 0008. Removing a symbol deactivates it and keeps its history

- **Status:** Accepted
- **Date:** 2026-11-03

## Context

Admins remove monitored symbols at runtime (`DELETE /api/admin/symbols/{symbol}`). The question is
what happens to the ticks and candles already stored, and what a later re-add means.

## Decision

- Removing a symbol **deactivates** it: `is_active = false` and `deactivated_at` is set. Ingestion
  unsubscribes it within one reconcile (immediately in-process, and at the latest after
  `Ingestion:ReconcileInterval` on another replica). The answer is `204 No Content`.
- The history stays. `24hAvgPrice`, `SimpleMovingAverage` and `candles` keep answering for an
  inactive symbol from what is stored.
- Re-adding it **reactivates** the same row (`201 Created`, with a `Location`). With backfill on, the
  stretch it was inactive is backfilled from Binance klines, so its candles have no hole.
- Raw ticks still follow the 14-day retention; candles are kept indefinitely.

## Alternatives

- **Hard delete with the data.** Rejected: one wrong call would destroy months of candles that
  Binance's REST limits make slow to restore, and nothing in the product needs the data gone.
- **Hard delete of the symbol, data kept as orphans.** Rejected: foreign keys and the read side
  would need special cases for symbols that no longer exist.

## Consequences

- The 20-symbol limit ([0009](0009-admin-api-key-fails-closed.md)) counts **active** symbols only.
- The repository keeps `deactivated_at` after a reactivation, because the backfill uses it to find the
  inactive stretch. The admin API shows `deactivatedAt` as `null` while a symbol is active, so clients
  never see a stale value (defect F14 in the [defects log](../ai/defects-log.md)). It also records
  `reactivated_at`: the stretch ends at the first minute streamed since the reactivation, not at the first
  stream row after `deactivated_at`, which the old stream can still have written when its UNSUBSCRIBE took
  effect late.
- `GET /api/symbols` lists inactive symbols too, with `isActive: false`, so clients can still find
  their history.
