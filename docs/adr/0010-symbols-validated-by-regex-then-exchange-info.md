# 0010. Symbols are validated by shape, then by Binance `exchangeInfo`

- **Status:** Accepted
- **Date:** 2026-11-03

## Context

Admins type symbol names by hand. A symbol that Binance does not trade would subscribe to a silent
stream, and a backfill for it would fail. Public routes also take a symbol in the path, where any
string can arrive.

## Decision

- Every symbol is trimmed, checked against `^[A-Za-z0-9]{5,20}$` (ASCII), then upper-cased
  (`SymbolName.TryCreate`). Symbols are therefore case-insensitive everywhere.
- When an admin adds a symbol, TickPulse also calls Binance `GET /api/v3/exchangeInfo?symbol=` and
  requires `status = TRADING`.

| Case | Answer |
|---|---|
| missing or blank `symbol` in the request body | `400 validation` with `errors.symbol` |
| malformed, unknown to Binance, or not `TRADING` | `422 invalid-symbol` |
| Binance cannot be reached or keeps failing | `503 upstream-unavailable` |
| already active | `200`, unchanged |
| added or reactivated | `201` with `Location: /api/admin/symbols/{SYMBOL}` |

- On the public routes a malformed or never-monitored symbol is `404 unknown-symbol`. Nothing is
  looked up at Binance for a read.

## Alternatives

- **Accept anything.** Rejected: typos would create dead subscriptions and failed backfills.
- **Regex only.** Rejected: `BTCUSDX` passes the regex.
- **Cache the full `exchangeInfo` list.** Rejected for now: it is a large document, and adding
  symbols is rare enough for a per-add call.

## Consequences

- Adding a symbol depends on Binance being reachable. A `503` is retryable and changes nothing.
- A symbol that Binance delists later keeps its row and history. Its stream simply goes quiet, which
  the zero-ticks alert reports.
