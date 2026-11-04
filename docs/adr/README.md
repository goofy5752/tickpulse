# Architecture decision records

Each record states one decision: its context, the decision, the alternatives that were rejected and
why, and the consequences. Records are not rewritten when a decision changes; a new record supersedes
the old one.

## Product behaviour

| # | Decision |
|---|---|
| [0001](0001-aggtrade-stream-as-price-feed.md) | The `aggTrade` stream is the price feed |
| [0002](0002-24h-average-is-the-arithmetic-mean.md) | The 24h average is the arithmetic mean of collected trades |
| [0003](0003-partial-window-reports-its-coverage.md) | With less than 24 hours of data, average what exists and report the coverage |
| [0004](0004-sma-data-point-is-the-bucket-close.md) | An SMA data point is the close of one complete, UTC-aligned bucket |
| [0005](0005-sma-start-parameter-anchors-the-window-start.md) | `s` anchors the start of the SMA window; without it the window ends now |
| [0006](0006-sparse-buckets-are-skipped-not-filled.md) | Fewer than `n` buckets or gaps: use what exists, skip empty buckets |
| [0007](0007-switchable-kline-backfill-for-sma-only.md) | A switchable kline backfill feeds long-period SMAs, never the 24h average |
| [0008](0008-removing-a-symbol-deactivates-it.md) | Removing a symbol deactivates it and keeps its history |
| [0009](0009-admin-api-key-fails-closed.md) | Admin endpoints use one API key and fail closed |
| [0010](0010-symbols-validated-by-regex-then-exchange-info.md) | Symbols are validated by shape, then by Binance `exchangeInfo` |

## Architecture

| # | Decision |
|---|---|
| [0011](0011-single-process-with-a-leader-lock.md) | One deployable process, with ingestion behind a PostgreSQL leader lock |
| [0012](0012-own-candle-table-not-a-continuous-aggregate.md) | `candles_1m` is our own table, not a continuous aggregate |
| [0013](0013-mvc-controllers-for-xml-negotiation.md) | MVC controllers, so JSON and XML are negotiated on every path |
| [0014](0014-raw-clientwebsocket-for-the-binance-stream.md) | A thin wrapper over `ClientWebSocket` for the Binance stream |

## Decisions made during implementation

| # | Decision |
|---|---|
| [0015](0015-candle-rollups-maintained-by-triggers.md) | Hourly and daily candle rollups, maintained by triggers |
| [0016](0016-request-sampling-with-429-as-a-sampled-response.md) | Request telemetry is sampled at the source; 429 is sampled like a success |
| [0017](0017-build-time-precompressed-static-assets.md) | Static assets are precompressed at build time; API responses are compressed at the edge |
| [0018](0018-tick-staging-in-a-session-temp-table.md) | Ticks are staged in a per-session temp table |
| [0019](0019-atomic-symbol-add-under-an-advisory-lock.md) | Adding a symbol is atomic under a transaction-scoped advisory lock |
| [0020](0020-hosted-workers-never-crash-the-host.md) | Hosted workers never crash the host |
| [0021](0021-xml-element-names-match-json-property-names.md) | XML element names match JSON property names |
| [0022](0022-gap-fill-watermarks-seeded-from-the-database.md) | Gap-fill watermarks are seeded from the database at every leadership session |
