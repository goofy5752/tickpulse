---
name: tickpulse-architecture
description: Use when adding or changing code under `src/` in the TickPulse repo - new types, use cases, ports, SQL, workers, controllers, DTOs or CLI commands - or when deciding which project something belongs in. Holds the layer rules, the placement guide, and the ingestion invariants (idempotent ticks, single-statement candle upsert, leader lock) that must never be broken.
---

# TickPulse Architecture

## Layers

```
Domain ← Application ← Infrastructure ← Api        Contracts ← Api        Contracts ← Cli
```

| Project | May reference | Must never reference |
|---|---|---|
| `TickPulse.Domain` | nothing (BCL only) | every other project, EF Core, Npgsql, ASP.NET Core, `System.Net.Http`; no I/O (by convention) |
| `TickPulse.Application` | Domain | Infrastructure, Contracts, Api, Cli, EF Core, Npgsql |
| `TickPulse.Infrastructure` | Application, Domain | Api, Cli |
| `TickPulse.Contracts` | nothing (BCL only) | every other project |
| `TickPulse.Api` | Infrastructure, Application, Domain, Contracts | Cli |
| `TickPulse.Cli` | Contracts | Domain, Application, Infrastructure, Api; never the DB |

`tests/TickPulse.Architecture.Tests/LayeringTests.cs` enforces this table twice: NetArchTest
checks type-level dependencies in the built assemblies, and the csproj `ProjectReference`s are
parsed so that an unused but forbidden reference also fails. Adding a project under `src/` fails
a test until you add its rules there, in the same PR.

## Where things go

| Thing | Project / folder |
|---|---|
| `Period`, bucket alignment (`AlignDown`/`AlignUp`, week origin `2000-01-03T00:00:00Z`), `SymbolName`, `SmaWindowCalculator`, `SmaCalculator`, `AverageCalculator`, `Tick`, `Kline`, `PricePoint` | `Domain/` |
| Ports (`ITickWriter`, `ICandleWriter`, `ISymbolRepository`, `IMarketDataQueries`, `IExchangeMetadata`, `ISymbolChangeSignal`, `IBackfillQueue`, …), `AppResult`, use cases (`AnalyticsService`, `SymbolAdminService`), `IngestionState` | `Application/` |
| EF Core `TickPulseDbContext` (symbols only), migrations incl. Timescale DDL via `migrationBuilder.Sql`, Npgsql writers/queries, leader lock | `Infrastructure/Persistence/` |
| Binance REST + WS clients, stream parser, subscribe throttle | `Infrastructure/Binance/` |
| Ingestion worker, batch writer, reconciler, gap-filler, backoff | `Infrastructure/Ingestion/` |
| Backfill worker | `Infrastructure/Backfill/` |
| Meters (`TickPulse.*`) | `Infrastructure/Telemetry/` |
| DI wiring | `Infrastructure/DependencyInjection.cs` → `AddTickPulseInfrastructure` |
| Request/response DTOs, `ProblemResponse` | `Contracts/` (JSON + `DataContract` XML, `DateTime` UTC) |
| Controllers, ProblemDetails writers, API-key auth, SignalR hub, rate limiting, health, OpenAPI/Scalar, static dashboard | `Api/` |
| Commands, typed API client, rendering | `Cli/` |

Rules of thumb:
- Pure computation with no I/O → Domain, with unit tests first.
- Orchestration that needs a port → Application; the port's implementation → Infrastructure.
- Anything both Api and Cli serialise → Contracts. The CLI parses problem bodies into
  `Contracts.ProblemResponse`, never ASP.NET's `ProblemDetails`.
- Controllers stay thin: validate, call the use case, map `AppResult` to a DTO or a problem.
- Money is `decimal`; time comes from an injected `TimeProvider`.

## HTTP surface (stable)

- `GET /api/{symbol}/24hAvgPrice`, `GET /api/{symbol}/SimpleMovingAverage?n=&p=&s=` (routes fixed).
- MVC controllers (minimal APIs have no XML formatters). `AddXmlDataContractSerializerFormatters`,
  `ReturnHttpNotAcceptable = true`.
- Every error, including middleware errors (401, 429, 404 unmatched, 500, 406), is a
  ProblemDetails body negotiated as JSON or XML through the custom XML `IProblemDetailsWriter`.

## Ingestion invariants (never break these)

1. **Idempotent ticks.** `price_ticks` PK is `(symbol_id, agg_trade_id, trade_time)`. Every insert
   is `ON CONFLICT DO NOTHING`. Replays after reconnect, rotation overlap and gap-fill must be
   harmless. Ticks older than `TickWriter.ReplayWindow` (raw retention minus one day) are never
   inserted: once retention dropped them they would no longer conflict and would be counted twice.
2. **Single-statement candle upsert.** Per batch, in one transaction:
   `CREATE TEMP TABLE IF NOT EXISTS tick_staging (LIKE price_ticks INCLUDING DEFAULTS) ON COMMIT DELETE ROWS`
   (a session table, so concurrent writers never see each other's rows; there is no permanent
   staging table), binary `COPY` into `tick_staging`, then **one** statement:
   `WITH ins AS (INSERT INTO price_ticks … ON CONFLICT DO NOTHING RETURNING …), agg AS (…)
   INSERT INTO candles_1m … ON CONFLICT (symbol_id, bucket_start) DO UPDATE …`.
   Candles aggregate only the rows actually inserted (`RETURNING`), so `price_sum`/`tick_count`
   stay exact and `24hAvgPrice` equals a raw `AVG(price)`. Never update candles from the batch
   input directly, and never in a second statement.
3. **Merge rules.** open/close by `first_trade_id`/`last_trade_id`, high/low by
   `GREATEST`/`LEAST`, sums add. A stream row (`source=1`) replaces a backfill row
   (`source=2`). Backfill inserts use `ON CONFLICT DO NOTHING` with NULL sums and ids, and never
   feed `24hAvgPrice`.
4. **Leader lock.** Only the holder of `pg_try_advisory_lock` on a dedicated connection ingests and
   backfills; other replicas serve the API and retry every 10 s. Losing the connection means
   losing leadership: stop ingesting.
5. **One WebSocket connection**, runtime `SUBSCRIBE`/`UNSUBSCRIBE` at ≤ 5 messages/s, rotation at
   ~23 h make-before-break, reconnect with jittered exponential backoff (cap 30 s), gap-fill via
   REST `aggTrades?fromId=last+1`. Every leadership session seeds the gap-fill watermarks from
   the DB (the newest stored trade's `agg_trade_id` per active symbol within `IngestionWorker.SeedWindow`,
   12 days: the replay window less a day of headroom for the fill; one ordered-ChunkAppend probe that
   never decompresses older chunks) before it subscribes, so the gap
   after the last stored trade is filled after a restart or handover. `GapFillWatermarks` keeps,
   per symbol, the end of the contiguous prefix of accounted ids (`LastId`) and one rule: it
   moves up only across ids that were delivered by this process or stored by another instance.
   A connection's span raises it only when contiguous with it; a span that starts higher (the
   connection it replaced stalled) opens a gap instead, checked on every reconcile pass for the
   live connection too. The seed raises an older in-memory watermark to the stored id only when
   nothing this process delivered lies above it (the highest id any ended connection delivered,
   including a discarded rotation link, or that a dropped batch or lead-in reported missing);
   otherwise the stored id may be this process's own stream above the gap, and the fill walks up from
   the watermark. The stored id cannot see holes *below* it: one that only a crashed or other process
   knew about (its dropped batch, its capped fill) is not recovered by the seed. A gap still
   open on the live connection is filled again on every reconcile pass (one run per symbol at
   a time). A clean rotation hands the retired link's last ids to the new link: until the new link
   delivers a symbol, a short REST page completes its fill only past them. A symbol that starts
   streaming with no watermark gets a lead-in from the start of its first minute
   (`aggTrades?startTime`), so that candle is not partial; a failed lead-in is retried on the
   next connection. Symmetrically, a removed symbol gets a one-shot tail-out after its UNSUBSCRIBE
   ack: once its last minute is over, REST fills from the last delivered id + 1 (or the id handed
   over by a rotation, if the link has delivered none yet) up to that minute's end (background,
   bounded, best-effort; it never delays the unsubscribe and never writes past that minute). After
   an outage longer than the seed window the trades of that stretch are not recovered; with backfill
   enabled, every leadership session fills its candles from klines.
   Symbol changes signal ingestion in-process and are also reconciled against the DB every 30 s.
6. **Bounded channel** with `FullMode=Wait` (backpressure, never drop). Batches flush at 500 ms or
   5,000 ticks.

## Persistence notes

- EF Core maps only `symbols` (snake_case via `UseSnakeCaseNamingConvention`). Ticks and candles go
  through Npgsql raw SQL.
- Higher periods aggregate from `candles_1m` in SQL with `time_bucket(width, bucket_start, origin)`
  and `last(close, bucket_start)`; week origin `2000-01-03T00:00:00Z`.
- Compression/retention are Timescale community features; queries must not depend on them.
- Clear tables with `DELETE FROM`.
