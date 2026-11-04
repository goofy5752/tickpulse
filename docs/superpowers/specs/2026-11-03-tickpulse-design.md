# TickPulse — Design

**Date:** 2026-11-03
**Status:** Accepted
**Repo:** `github.com/goofy5752/tickpulse`
**Live:** `https://tickpulse.dev` · Grafana `https://grafana.tickpulse.dev`

---

## 1. Intent

Build a .NET market-data service that collects Binance spot prices over WebSocket, stores them in a relational DB, and serves analytics (24h average, SMA) over a JSON/XML HTTP API. Admins manage the monitored symbols at runtime. A console client offers the same analytics.

**Quality bar:** production-grade, not a demo. In practice that means:

1. It works, live, at a public URL, and locally with **one command**.
2. Every requirement is met exactly. Wherever a requirement allows more than one reading, the chosen interpretation is explicit and justified, with the rejected alternative recorded (ADRs).
3. It is correct under real-world conditions: reconnects, duplicates, 24h connection expiry, rate limits, decimal precision.
4. It is well tested: real-DB integration tests and a fake Binance server.
5. AI use is visibly disciplined: project context files, a curated prompt log, a validation story, and a defects-caught log.

The documentation follows the order a new engineer would take when getting to know the project: README → `engineering-notes.md` → `docker compose up` → ingestion/SMA code and tests → the live URL.

## 2. Goals, non-goals, anti-goals

**Goals: all in scope.** The order is the build order, not a cut list.
1. Backend core: ingestion → DB → `24hAvgPrice` + `SimpleMovingAverage` with JSON/XML → admin API → tests.
2. Console client.
3. Docker compose, README, `engineering-notes.md`, prompt log.
4. Live deployment + GitHub Actions CI/CD.
5. Full observability: logs, metrics and traces in Grafana on its own subdomain (§8.1).
6. Modern dashboard (design direction "B — Aurora Glass", §9).
7. REST gap-fill after reconnect (zero data loss).

**Non-goals:** user accounts, order books, trading, multi-exchange, mobile, i18n.

**Anti-goals (failure even if it "works"):**
- Microservices / message brokers for a two-endpoint problem. Complexity has to earn its place.
- A flashy frontend masking a weak backend.
- Fabricated or embellished prompts in the prompt log.
- Silent behaviour beyond the core requirements. Anything extra (e.g. backfill) must be documented and switchable.
- Touching other tenants' services or configs on the shared VPS.

## 3. Constraints

- .NET 10 (LTS), C#. A relational DB is required; chosen: **PostgreSQL 17 + TimescaleDB** (community image `timescale/timescaledb`).
- Public API routes (stable contract; case-insensitive symbol):
  - `GET /api/{symbol}/24hAvgPrice`
  - `GET /api/{symbol}/SimpleMovingAverage?n={n}&p={p}&s={s}`, where `p ∈ {1w, 1d, 30m, 5m, 1m}`
- JSON and XML via HTTP content negotiation (`Accept` header).
- Initial symbols: BTCUSDT, ADAUSDT, ETHUSDT (seeded by migration).
- Documentation in the repo: README, `engineering-notes.md`, an AI prompt record, a log of issues, assumptions and defects, and a deployment guide.
- The prompt record (`docs/ai/prompts.md`) holds faithful English translations of the original prompts, which were written in Bulgarian. They are lightly edited for clarity and never invented. `engineering-notes.md` states this.
- Hosting: an existing shared DigitalOcean VPS (Frankfurt; 4 vCPU, 7.8 GB RAM with ~3.8 GB free, ~25 GB free disk). Binance REST was verified reachable from it. Because it is shared, TickPulse uses its own directory, compose project and network, with memory limits on every container.

## 4. Design decisions on open questions (each becomes an ADR in `docs/adr/`)

| # | Open question | Decision | Rejected alternatives |
|---|---|---|---|
| 1 | Which stream is a "price update" | `<symbol>@aggTrade`: real executed trades, compressed per taker order | `@trade` (noisier, same info); `@miniTicker` / `@avgPrice` (pre-aggregated 1 s snapshots, not price updates); `@kline_1m` (loses per-trade granularity for the 24h average) |
| 2 | What the "average price" over 24h is | **Arithmetic mean of all collected price updates** in `[now−24h, now]` | TWAP, VWAP (documented as alternatives; Binance's own `avgPrice` is VWAP-like) |
| 3 | Less than 24h of data | Average over the available ticks. The response reports the actual `from`/`to`, the covered duration and the sample count | Error / 404 |
| 4 | What an SMA "data point" is | The **close** (last trade price) of one complete, UTC-aligned `p` bucket. `1w` buckets start Monday 00:00 UTC (Binance convention) | Mean price of the bucket |
| 5 | `s` is defined as the starting date, while the current time is the reference point when it is omitted | Honour both definitions. With `s`, the window **starts** at the first bucket whose start ≥ `s` and extends forward n buckets, clipped at the last complete bucket. Without `s`, the window is the **last n complete buckets ending at now** | `s` as the end anchor in both cases |
| 6 | Fewer than n buckets available, or gaps (outage) | Use the available non-empty buckets. Return `requestedDataPoints`, `actualDataPoints` and the data points themselves. Empty buckets are skipped, not forward-filled. Zero points → 404 ProblemDetails (`no-data`) | Error when < n; forward-fill |
| 7 | 1w/1d SMAs are useless with days of data | **Kline backfill**: when a symbol is added (and on first start for seeded ones), fetch 1m klines from Binance REST into the candle table, marked `source=backfill`. Config flag `Backfill:Enabled`: **off** in base `appsettings.json` (pure collected-data mode), **on** in docker-compose and live. Backfilled candles feed SMA only, never `24hAvgPrice`, which stays strictly "collected data" | No backfill (long-period SMAs show "1 of 20 points" for days) |
| 8 | Removing a symbol | Deactivate: stop the subscription, keep the history. Queries on inactive symbols still work. Re-adding reactivates | Hard delete with data |
| 9 | Admin security | `X-Api-Key` header, constant-time compare. Key from env. **No key configured → admin endpoints disabled (fail closed)**. Max active symbols: 20 (config) | No auth; full identity |
| 10 | Symbol validity | Regex `^[A-Z0-9]{5,20}$`, then Binance `GET /api/v3/exchangeInfo?symbol=` must return `status=TRADING`. Unknown symbol → 422 | Accept anything |

## 5. Architecture

A single deployable process (API + hosted ingestion worker). This trade-off is documented, together with how to split the worker out.

```
src/
  TickPulse.Domain          # pure: Symbol, Period (1m..1w, alignment incl. Monday weeks),
                            # bucket math, SMA calculator, average calculator. No deps.
  TickPulse.Application     # use cases/queries, interfaces (ITickStore, ICandleStore,
                            # ISymbolRepository, IExchangeMetadata, IClock via TimeProvider)
  TickPulse.Infrastructure  # EF Core (symbols, migrations), Npgsql raw SQL + binary COPY
                            # (ticks/candles), Binance WS client, Binance REST client
                            # (exchangeInfo, klines, aggTrades), leader lock
  TickPulse.Contracts       # DTOs shared by API and CLI (XML/JSON serializable)
  TickPulse.Api             # controllers, content negotiation, ProblemDetails, admin auth,
                            # SignalR hub, health, OpenAPI + Scalar, rate limiting, static dashboard
  TickPulse.Cli             # console client
web/                        # React dashboard (built into Api wwwroot in the Docker image)
tests/
  TickPulse.Domain.Tests    # TDD unit tests for all math/alignment
  TickPulse.Application.Tests
  TickPulse.Integration.Tests  # Testcontainers TimescaleDB + WebApplicationFactory + fake Binance WS
  TickPulse.Cli.Tests
```

**Layering:** Domain ← Application ← Infrastructure; Api is the composition root. NetArchTest enforces the layering (cheap and convincing).

### 5.1 Ingestion pipeline

```
Binance WS (combined stream, dynamic SUBSCRIBE)
  → reader (parses aggTrade JSON; decimal prices from strings)
  → bounded Channel<Tick> (capacity ~100k, FullMode=Wait → backpressure)
  → batch writer (flush every 500 ms or 5k ticks):
       binary COPY into a staging table → INSERT … ON CONFLICT DO NOTHING into price_ticks
       → upsert of the affected 1m candles from that batch (merge OHLC/sum/count)
  → live broadcaster (latest price per symbol → SignalR, throttled ~4 Hz)
```

- **One connection** to `wss://stream.binance.com:9443/stream`. `SUBSCRIBE` / `UNSUBSCRIBE` are sent at runtime, respecting the limit of 5 incoming messages per second and 1024 streams per connection.
- **24h connection expiry:** rotate proactively at ~23h, make-before-break (open the new connection, subscribe, then close the old one). PK dedup absorbs the overlap.
- **Reconnect:** exponential backoff with jitter (cap 30 s). Ping/pong is handled (the server pings every 20 s).
- **Idempotency:** PK `(symbol_id, agg_trade_id, trade_time)`, so replays and overlaps never duplicate.
- **Leader election:** `pg_try_advisory_lock` on a dedicated connection. Only the lock holder ingests; other replicas serve the API and retry every 10 s.
- **Symbol changes:** the admin use case writes to the DB and signals the ingestion service in-process. Ingestion also reconciles against the DB every 30 s, which covers changes made by other replicas.
- **Gap-fill (goal 7):** after a reconnect, fetch the missed trades via REST `aggTrades?fromId=last+1`. Every leadership session (start-up, restart, redeploy, handover) first seeds the starting points from the database: each active symbol's highest stored `agg_trade_id` of the last 24 h. So the trades that happened while no instance led are recovered too. Each starting point only ever moves up across trades this instance delivered or another instance stored: a connection's stream raises it only when contiguous with it, and a stream that starts higher (the connection it replaced had stalled) opens a gap for the trades between, noticed on the next reconcile pass. An instance that leads again raises its own older starting points to the stored id only when nothing it delivered lies above them (on any connection, including a discarded rotation link, or reported missing by a dropped batch); otherwise the stored maximum may be its own stream above the gap, and the fill walks up from the starting point. The stored maximum cannot reveal holes below it, such as a dropped batch that only a crashed instance knew about. A connection that breaks reopens the gap for a fill it had bounded. A gap still open on the live connection is filled again on every reconcile pass until it closes. A symbol that starts streaming with nothing known (first start, added, reactivated) is filled from the start of the minute it was subscribed in (`aggTrades?startTime`) up to its first streamed trade. Its first candle is then complete.

### 5.2 Data model (PostgreSQL + TimescaleDB)

- `symbols(id, name UNIQUE, is_active, created_at, updated_at, deactivated_at, backfill_status, backfilled_from)`, managed through EF Core with migrations and the seed.
- `price_ticks(symbol_id, agg_trade_id, price numeric(24,8), quantity numeric(28,8), trade_time timestamptz, ingested_at)`, a hypertable on `trade_time` (1-day chunks), compressed after 2 days, retention 14 days.
- `candles_1m(symbol_id, bucket_start, open, high, low, close, first_trade_time, last_trade_time, price_sum, tick_count, volume, quote_volume, source)`, PK `(symbol_id, bucket_start)`. This is our own table, **not** a continuous aggregate, because backfill must write into it. Kept forever (~1,440 rows per symbol per day).
- **Licensing note:** the compression and retention policies on `price_ticks` are Timescale *community-licensed* (TSL) features. They are available in the `timescale/timescaledb` image we run, but not on Apache-only managed offerings. The design therefore degrades gracefully: the query logic doesn't depend on them, and without them retention becomes a scheduled `DELETE`/chunk drop, at the cost of a larger storage footprint. This goes in the ADR and the scale-out design.
  - Stream upserts merge (open = earliest trade, close = latest, sum and count add up) and replace `backfill` rows.
  - Backfill inserts use `ON CONFLICT DO NOTHING`. `price_sum`/`tick_count` are NULL for backfill rows.
- Higher periods are aggregated in SQL from `candles_1m` (`time_bucket` with a Monday origin for 1w; close = last close by bucket_start).
- **24hAvgPrice (exact and fast):** `Σprice_sum / Σtick_count` over the full `source=stream` minutes in the window, plus raw ticks for the two partial edge minutes. The result is identical to a raw `AVG(price)` (an integration test proves it), at a fraction of the cost.
- Backfill depth: configurable, default 26 weeks (enough for the 1w SMA with n=20). About 260 REST calls per symbol at limit=1000, well under the weight limits.

### 5.3 HTTP API

- **MVC controllers** (minimal APIs have no XML output formatters; this is a documented trade-off). Uses `AddXmlDataContractSerializerFormatters`, `ReturnHttpNotAcceptable = true` (→ 406) and RFC 9457 ProblemDetails in both formats.
  - Content negotiation only covers controller `Problem()` results. Errors produced by middleware go through `IProblemDetailsService`, whose default writer is JSON-only. That covers 401 from the auth handler, 429 from the rate limiter, unhandled exceptions, unmatched-route 404s and the empty-bodied 406.
  - **Required:** a custom XML `IProblemDetailsWriter`, chosen by `Accept`, plus a ProblemDetails body for the 406, so *every* error honours negotiation. Tests cover each of these paths.
- Public endpoints:
  - the two required routes
  - `GET /api/symbols`
  - `GET /api/{symbol}/candles?p=&limit=` (dashboard)
  - `GET /api/system/stats`
- Admin endpoints (`X-Api-Key`):
  - `GET /api/admin/symbols`
  - `POST /api/admin/symbols {symbol}`: validates and activates; backfill runs async; returns 201/200
  - `DELETE /api/admin/symbols/{symbol}`: deactivates; returns 204
- SignalR hub `/hubs/market` (groups per symbol; live prices and stats).
- Validation:
  - `n`: 1..1000
  - `p`: one of the five values
  - `s`: ISO-8601; without an offset it is treated as UTC; a future `s` → 400
- Errors: unknown symbol → 404 (`unknown-symbol`); no data → 404 (`no-data`).
- Built-in rate limiter on the public routes, keyed on the client IP via ForwardedHeaders (`CF-Connecting-IP` behind Cloudflare).
- OpenAPI (built-in .NET 10) + Scalar UI at `/docs`.
- Health: `/health/live`, and `/health/ready`, which needs the DB plus a connected WS (or "standby" when not the leader).
- `decimal` everywhere for prices. `TimeProvider` for all clock reads.

**SMA response shape (illustrative):** `symbol, period, requestedDataPoints, actualDataPoints, value, windowStart, windowEnd, referenceTime, dataPoints[{bucketStart, close}]`. **24h response:** `symbol, averagePrice, from, to, coveredSeconds, sampleCount`. The builder finalises the names, keeping them consistent between JSON and XML.

### 5.4 Console client (`TickPulse.Cli`)

- Built with `System.CommandLine` + `Spectre.Console`, using a typed HttpClient against the HTTP API only (never the DB).
- Commands:
  - `avg <symbol>`
  - `sma <symbol> -n -p [-s]`
  - `symbols list|add|remove` (admin key via `--api-key` / env)
  - `watch <symbol>` (live refresh)
- Global options:
  - `--api-url`: default from env `TICKPULSE_API_URL`, else `https://tickpulse.dev`
  - `--format table|json|xml`: json/xml set the `Accept` header and print the raw body, which shows the negotiation
- Meaningful exit codes. Runnable via `dotnet run` and via `docker compose run cli …`.

## 6. Testing

- **Domain (TDD):**
  - period alignment, including Monday weeks, DST-free UTC and bucket boundaries
  - the SMA window for both `s` cases
  - insufficient and gappy data
  - average merge math
  - coverage gate ≥ 95%
- **Integration (Testcontainers TimescaleDB, real migrations):**
  - exact 24h average equals the raw `AVG(price)`
  - SMA over each period against hand-computed fixtures
  - dedup on replay
  - candle merge across batches
  - backfill rows never affect the 24h average
- **API:**
  - JSON vs XML vs 406 for every endpoint
  - ProblemDetails in XML
  - admin auth: missing key → 401, wrong key → 401, no key configured → disabled
  - validation errors
- **Fake Binance WS server** (Kestrel in-test):
  - SUBSCRIBE/UNSUBSCRIBE at runtime
  - forced disconnect → reconnect without loss or duplicates
  - rotation overlap
  - malformed messages are logged and skipped
- **Architecture tests:** layering rules.
- CLI tests against an in-memory test server.
- The overall coverage gate is set in CI. Exact thresholds beyond Domain are left to the builder (sensible: ≥ 80% overall).

## 7. Delivery process and AI workflow

- Repo: `goofy5752/tickpulse`, public, default branch `main`.
- Flow: one git worktree per change → `feat|fix|chore|docs/<slug>` → PR to `main` → CI required → squash merge. Scoped Conventional Commits. No direct commits to `main` after bootstrap. Parallel subagents each work in their own worktree.
- Repo-resident AI context (documents how agents are given context and constraints):
  - `CLAUDE.md`
  - `.claude/skills/tickpulse-pr-workflow`
  - `tickpulse-architecture`
  - `tickpulse-testing`
- Docs:
  - `README.md`: what it is, live links, a one-command quick start, configuration, DB initialisation (automatic migrations + seed), running the CLI, running the tests
  - `engineering-notes.md`: approach, key decisions (linking the ADRs), AI workflow and how generated code was validated (TDD, real-DB tests, review passes, manual verification), defects/issues caught, trade-offs, what we would do next
  - `docs/adr/NNNN-*.md`: one per row of §4, plus the architecture choices
  - `docs/deployment.md`: what is live and how, plus an **scale-out production design**:
    - Azure Container Apps (API scaled out; ingestion as a singleton via the advisory lock)
    - managed PostgreSQL. Caveat: Azure Flexible Server offers at most Apache-licensed Timescale, which has no compression or retention policies. **Verify against current Azure docs whether TimescaleDB is offered on PG 17 at all before writing it.** The options are Timescale Cloud, or plain PG with native partitioning and scheduled chunk drops
    - Key Vault, App Insights/OTel, blue-green deploys
  - `docs/ai/prompts.md`: curated prompt record, extracted from the actual Claude Code session transcripts. It starts with the design/brainstorm session of 2026-11-03 and is grouped by phase. Each entry gives the prompt (translated), its intent and the outcome.
  - `docs/ai/defects-log.md`, maintained continuously during development and summarised in the engineering notes

## 8. CI/CD and hosting

- **CI (every PR):**
  - `dotnet build`, `dotnet format --verify-no-changes`, all tests (Testcontainers on the Ubuntu runner), coverage report and gate
  - web: lint, typecheck, build
  - Docker image build
  - Dependabot enabled
- **CD (push to `main`):**
  1. Build and push `ghcr.io/goofy5752/tickpulse-api:{sha}` and `:latest`.
  2. SSH to the droplet and run `docker compose pull && up -d` in `/srv/tickpulse`.
  3. Smoke test: `/health/ready`, `24hAvgPrice` JSON, `Accept: application/xml`.
  4. On failure, automatically roll back to the previous sha.
- **Droplet layout:**
  - `/srv/tickpulse` with its own compose project: `api` (serves the dashboard) + `timescaledb`, with memory limits (≈512 MB each)
  - the API is published on loopback only
  - host nginx vhosts for `tickpulse.dev` and `grafana.tickpulse.dev`, Cloudflare proxied with an Origin cert
  - secrets in `/srv/tickpulse/.env` (chmod 600) and GitHub Environment secrets
  - containers are run by an unprivileged `deploy` user (docker group, no sudo)
- **Storage estimate:** < 2 GB for raw ticks (14-day retention, compressed) plus candles.
- **Shared-host hygiene:** a new `server_name`-scoped vhost file, then `nginx -t`, then a graceful `reload`, so other tenants see no disruption and their configs are never edited.
  - Until DNS is live, deploy and smoke-test over an SSH tunnel to loopback.
  - The CLI default URL stays `https://tickpulse.dev`.
- **Lifetime:** the site is meant to stay up long-term.
  - Docker restart policies + health checks.
  - The ingestion worker self-heals (reconnect/leader retry).
  - A free external uptime check (e.g. UptimeRobot on `/health/ready`) alerts the maintainer if it goes down.
- **Local:** `docker compose up` brings up the DB, API and dashboard, runs the migrations and seed, and runs the backfill (on in compose). `docker compose run cli avg BTCUSDT` works out of the box.

### 8.1 Observability (Grafana at `grafana.tickpulse.dev`)

- **Signals, all through OpenTelemetry:**
  - Structured logs: Serilog with message templates and enrichers (service, version, env, symbol, trace_id/span_id), exported via OTLP.
  - Metrics: ASP.NET Core and runtime meters plus custom `TickPulse.*` meters:
    - ticks received/persisted per symbol
    - ingest lag (trade_time → persisted)
    - channel depth
    - batch size and flush duration
    - WS connects/reconnects/rotations and current state
    - leader status
    - backfill progress
    - gap-fill trades recovered
    - API requests by route, status and **negotiated content type** (JSON vs XML)
  - Traces: incoming HTTP, Npgsql, outgoing Binance REST, and custom spans for batch flushes and backfill pages.
- **Stack** (its own compose project, sharing nothing with other tenants on the host): a collector (Grafana Alloy or the OTel Collector; builder's choice) → **Loki** (logs), **Tempo** (traces) and **Prometheus** (metrics) → **Grafana**. Logs ↔ traces are linked by trace_id (derived fields / Tempo data links). Memory-limited containers, with retention of 14 days for logs and traces and 30 days for metrics.
- **Everything provisioned as code** (`deploy/observability/`): datasources, plus dashboards as JSON:
  1. **TickPulse — Ingestion**: ticks/sec per symbol, ingest lag p50/p95, WS state timeline, reconnects/rotations, channel depth, flush duration, DB write rate
  2. **TickPulse — API**: RED per route (rate, errors, p50/p95/p99 latency), status codes, JSON vs XML split, rate-limit rejections
  3. **TickPulse — Runtime/DB**: GC, threadpool, memory, Npgsql pool, query durations
  4. **Logs explorer**: a filterable Loki panel with trace jump links
- **Alert rules (provisioned):**
  - WS disconnected > 60 s (alert after a 30 s pending period)
  - ingest lag p95 > 5 s
  - zero ticks for an active symbol for 2 min
  - API 5xx ratio > 2%
  - Contact point: the maintainer's email (configured via env, not committed).
- **Access:** Grafana is served behind the host nginx + Cloudflare like the API.
  - Anonymous **Viewer** access is limited to the TickPulse dashboards, so anyone can open them from the footer link. Explore and editing are disabled for anonymous users.
  - The admin login is in env only.
  - Logs never contain secrets. IPs are logged only as a hash.
- **Locally:** `docker compose --profile observability up` brings up the same stack with the same dashboards.

## 9. Dashboard (`web/`)

- **Stack:** React 19 + Vite + TypeScript + Tailwind 4 + shadcn/ui + TanStack Query + `lightweight-charts` (TradingView) + `@microsoft/signalr`. Pattern reference: LabForty `flex-dashboard`.
- **Visual direction "B — Aurora Glass":**
  - dark background with soft violet/teal radial glows and glass cards (translucent, 1px light border, blur)
  - teal = up, rose = down, violet = SMA
  - tabular numerals, Inter + JetBrains Mono
  - a light/dark toggle (dark is the default)
- **Single scrolling page** (approved mockup):
  1. Nav: brand, Dashboard / API Docs / GitHub links, theme toggle, live WS status pill with ingest lag
  2. Live ticker tiles per symbol (price, 24h change, sparkline) plus a "+ symbol" tile
  3. Main chart: candles for the selected period, SMA line overlay, and the SMA calculation window shaded. Side panel: 24h average (with coverage and sample count) and SMA controls (n, p, s) showing the value and "x / n points"
  4. API playground: pick an endpoint and an Accept header (JSON/XML), see the real request (plus a copyable curl) and the raw response
  5. Monitored symbols admin: enter the API key (kept in sessionStorage), add (with backfill status) and remove
  6. System panel: ticks/sec, ingest lag, rows stored, uptime, last reconnect, rotation schedule
  7. Footer: product name, stack, links to the README, engineering notes, architecture docs, Grafana and GitHub.
- All numbers come from the real public API (no mocks).
- Responsive down to mobile. Respects `prefers-reduced-motion`.

## 10. Schedule (my estimate)

- **Phase 1:** repo bootstrap (CLAUDE.md, skills, CI skeleton), Domain (TDD), persistence + migrations, ingestion, the two endpoints with JSON/XML, admin API, integration tests, CLI, docker compose.
- **Phase 2:** backfill, gap-fill, deployment + CD, observability stack + Grafana dashboards, dashboard, docs (README, notes, ADRs, scale-out design, prompt record), a final whole-repo code review, and fixes.
- The full scope is planned; nothing is cut.
- Independent slices run as parallel subagents in separate worktrees.

## 11. Decided by the owner vs. left to the builder

**Decided by the owner:**
- full scope, no cuts
- Grafana with professional logging on its own subdomain
- kline backfill enabled (on live/compose)
- visual direction B and the 7-section layout
- the repo `goofy5752/tickpulse`, public
- the domain `tickpulse.dev`
- English prompt log as faithful, lightly polished translations

**AI-proposed, owner-approved in the design session:** everything in §4–§8.

**Left to the builder:**
- exact DTO field names
- the WS client implementation (raw `ClientWebSocket` wrapper vs `Websocket.Client`)
- batch sizes and timings within the stated ranges
- coverage thresholds beyond Domain
- shadcn component choices
- the exact rate-limit numbers
