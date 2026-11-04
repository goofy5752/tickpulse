# 0019. Adding a symbol is atomic under a transaction-scoped advisory lock

- **Status:** Accepted
- **Date:** 2026-11-03

## Context

`POST /api/admin/symbols` must enforce `Admin:MaxActiveSymbols` (20). The first design counted the
active symbols, then inserted. Two concurrent adds (two admins, a retry, two replicas) could both see
19 and both insert, ending at 21. Re-adding a deactivated symbol is also an add: it must reactivate
the existing row, not create a second one.

## Decision

`ISymbolRepository.AddOrReactivateAsync(name, maxActive, ct)` does everything in one transaction:

1. `SELECT pg_advisory_xact_lock(<key>)` serialises every add, across all replicas.
2. If the symbol is already active, return `AlreadyActive`.
3. Count the active symbols. At the limit, return `LimitReached`.
4. Otherwise insert it (`Created`) or reactivate the existing row (`Reactivated`), with `RETURNING`.

The lock is released by the commit. The outcome type is
`SymbolAddOutcome(MonitoredSymbol?, SymbolAddStatus)`, which the use case maps to `201`, `200` or
`409 symbol-limit`.

## Alternatives

- **Check, then act, without a lock.** Rejected: the race above.
- **`SERIALIZABLE` isolation with retries.** Rejected: correct, but every caller would need a retry
  loop for serialisation failures, for a lock that is held a few milliseconds.
- **A database constraint on the number of active rows.** Rejected: PostgreSQL has no declarative
  constraint over a row count; a trigger would be the same lock in a harder-to-read place.

## Consequences

- The limit holds under concurrency (`SymbolRepositoryTests.AddOrReactivate_Concurrent_NeverExceedsLimit`).
  An already active symbol answers `200` even at the limit.
- Exchange validation (`exchangeInfo`) happens before the transaction, so the lock is never held
  across a network call to Binance.
- After the commit, the backfill status is set and the backfill queued with
  `CancellationToken.None`, so a client that disconnects right after the commit cannot leave a symbol
  active but never backfilled.
