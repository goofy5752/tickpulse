# 0004. An SMA data point is the close of one complete, UTC-aligned bucket

- **Status:** Accepted
- **Date:** 2026-11-03

## Context

`GET /api/{symbol}/SimpleMovingAverage?n=&p=` averages `n` data points of period
`p ∈ {1w, 1d, 30m, 5m, 1m}`. What a "data point" is, and where buckets start, has to be fixed
before any number can be checked against another source.

## Decision

- A data point is the **close** (the last trade price) of one **complete** bucket of width `p`.
  The bucket that is still open never counts.
- Buckets are aligned in **UTC**: minutes, 5- and 30-minute buckets and days on their natural
  boundaries, and **weeks start Monday 00:00 UTC**, the Binance kline convention.
- C# (`Period.AlignDown`) and SQL (`time_bucket(width, ts, origin)`) use the same origin,
  `2000-01-03T00:00:00Z`, which is a Monday. That keeps the week boundaries identical in both.
- The SMA is the plain mean of those closes, rounded to 8 dp with `MidpointRounding.ToEven`
  (`SmaCalculator`).

## Alternatives

- **The mean price of each bucket.** Rejected: it is not what an SMA over a period conventionally
  uses, and its values would not match any charting tool or Binance's klines.
- **Including the open bucket.** Rejected: its close changes with every trade, so the SMA would move
  between two identical requests.
- **Sunday weeks, or local-time alignment.** Rejected: they disagree with Binance's `1w` klines, and
  local time brings DST shifts.

## Consequences

- Values can be checked against Binance. In the end-to-end run against live Binance, BTCUSDT `1d n=20` was
  `80407.33600000` in TickPulse and over Binance's last 20 complete daily klines, with all 20 closes
  identical. `1w n=8`, `30m n=48` and the ETH, SOL and ADA periods matched too. A naive check with
  Binance `limit=20` differs, because Binance includes the open candle.
- `UTC` and the Monday origin are part of the API contract. `PeriodTests` pins the alignment on and
  off boundaries, before the origin, and for week buckets.
- 1d and 1w closes are read from a daily rollup table rather than aggregated from minutes on every
  request ([0015](0015-candle-rollups-maintained-by-triggers.md)).
