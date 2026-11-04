# 0003. With less than 24 hours of data, average what exists and report the coverage

- **Status:** Accepted
- **Date:** 2026-11-03

## Context

A freshly started instance, a newly added symbol or a symbol back from an outage has less than 24
hours of collected trades. `24hAvgPrice` still has to answer something useful, and the answer must
not pretend to cover a full day.

## Decision

Average the trades that exist in `[now − 24 h, now]` and say exactly what was averaged. The response
carries:

| Field | Meaning |
|---|---|
| `averagePrice` | the mean ([0002](0002-24h-average-is-the-arithmetic-mean.md)) |
| `from`, `to` | the trade times of the first and last trade averaged |
| `coveredSeconds` | `to − from`, in whole seconds |
| `sampleCount` | the number of trades averaged |
| `windowHours` | always `24`, the window that was asked for |

Only when the window holds no trade at all does the endpoint answer `404` with the `no-data` problem.
An unknown or malformed symbol is `404 unknown-symbol`.

## Alternatives

- **An error until a full day is collected.** Rejected: the endpoint would be useless for the first
  day after every symbol add, and clients could not tell "no data" from "not enough data".
- **Silently averaging what exists.** Rejected: a client could not tell a 5-minute average from a
  24-hour one.

## Consequences

- Clients decide for themselves whether the coverage is enough. The dashboard shows a coverage bar
  ("15m of 24h") next to the value, and the CLI prints the coverage with a note when it is partial.
- Coverage is measured between trades, so a quiet symbol can report less than 24 h even when it was
  collected all day. That is accurate: it is the span the trades cover.
