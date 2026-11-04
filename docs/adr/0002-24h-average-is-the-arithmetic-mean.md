# 0002. The 24h average is the arithmetic mean of collected trades

- **Status:** Accepted
- **Date:** 2026-11-03

## Context

`GET /api/{symbol}/24hAvgPrice` returns "the average price over the last 24 hours". An average price
can be defined in several ways, and they give different numbers on the same data:

- the **arithmetic mean** of every price update;
- the **VWAP**, which weights each trade by its quantity (Binance's own `avgPrice` and the rolling
  ticker's `weightedAvgPrice` are volume-weighted);
- the **TWAP**, which weights each price by how long it stood.

## Decision

The 24h average is the **arithmetic mean of all trades TickPulse collected** in `[now − 24 h, now]`,
both ends inclusive. The final value is rounded to 8 decimal places with `MidpointRounding.ToEven`
(`AverageCalculator`), in `decimal` throughout.

To keep it exact and fast, the query does not scan raw ticks for the whole window:

- whole minutes inside the window come from the stream candles, as `Σ price_sum / Σ tick_count`;
- the two partial minutes at the edges come from the raw ticks;
- candles written by the backfill ([0007](0007-switchable-kline-backfill-for-sma-only.md)) carry no
  `price_sum` or `tick_count` and are excluded, so the average stays "collected data only".

## Alternatives

- **VWAP.** Rejected as the default. It is a useful statistic, but "average price over the price
  updates" reads most directly as a plain mean, and VWAP would need quantities summed with the prices
  in every candle anyway. It stays an easy extension (`quote_volume / volume` is already stored).
- **TWAP.** Rejected: it needs a sampling rule for quiet periods and is sensitive to gaps in collection.

## Consequences

- The value is reproducible from the stored ticks.
  `MarketDataQueriesTests.Average_EqualsRawAvg_WithDuplicatesReplayed` checks the candle-based query
  against a raw `AVG(price)` over `price_ticks`, with duplicates replayed.
- It differs from Binance's volume-weighted figures, by design. The README says so.
- Raw ticks are kept for 14 days. Since the 24h window only needs the last day, retention never
  affects the result.
