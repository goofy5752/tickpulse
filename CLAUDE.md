# TickPulse — agent context

TickPulse is a .NET 10 market-data service. It ingests Binance spot trades (`<symbol>@aggTrade`)
over one WebSocket connection into PostgreSQL 17 + TimescaleDB, and serves analytics over a JSON/XML
HTTP API:

- `GET /api/{symbol}/24hAvgPrice` — arithmetic mean of collected trades in `[now−24h, now]`
- `GET /api/{symbol}/SimpleMovingAverage?n=&p=&s=` — SMA of bucket closes, `p ∈ {1w,1d,30m,5m,1m}`

Admins add/remove monitored symbols at runtime (`X-Api-Key`). A console client (`TickPulse.Cli`)
offers the same analytics, a React dashboard (`web/`) is served by the API, and telemetry goes over
OTLP to Grafana. One deployable process: the API plus hosted ingestion and backfill workers.

## Layers (enforced by `tests/TickPulse.Architecture.Tests`)

```
Domain ← Application ← Infrastructure ← Api      Contracts ← Api      Contracts ← Cli
```

- **Domain**: pure math and types (`Period`, bucket alignment, SMA window/calculator, averages).
  References nothing. No I/O, no EF Core, no Npgsql.
- **Application**: use cases and ports (`ITickWriter`, `ICandleWriter`, `ISymbolRepository`,
  `IMarketDataQueries`, `IExchangeMetadata`, …). References only Domain.
- **Infrastructure**: EF Core (symbols, migrations), Npgsql raw SQL + binary `COPY`
  (ticks/candles), Binance WS/REST clients, leader lock, hosted workers.
- **Contracts**: DTOs shared by Api and Cli (JSON + `DataContract` XML). References nothing.
- **Api**: composition root — controllers, content negotiation, ProblemDetails, admin auth,
  SignalR hub, health, OpenAPI + Scalar, rate limiting, static dashboard.
- **Cli**: talks to the HTTP API only. References only Contracts, never the DB.

Details and where new code goes: skill `tickpulse-architecture`.

## Commands

```bash
dotnet restore TickPulse.slnx && dotnet tool restore
dotnet build TickPulse.slnx -c Release
dotnet format TickPulse.slnx --verify-no-changes          # CI fails on any diff
dotnet test TickPulse.slnx                                # needs Docker running (Testcontainers)
dotnet test tests/TickPulse.Domain.Tests                  # fast, no Docker

# coverage, as CI computes it
dotnet test TickPulse.slnx -c Release --collect "XPlat Code Coverage" --results-directory coverage
dotnet tool run reportgenerator -reports:"coverage/**/coverage.cobertura.xml" \
  -targetdir:coverage/report -reporttypes:"MarkdownSummaryGithub;JsonSummary"

dotnet run --project src/TickPulse.Api                    # API (needs ConnectionStrings:TickPulse)
dotnet run --project src/TickPulse.Cli -- avg BTCUSDT     # console client
dotnet pack src/TickPulse.Cli -c Release                   # `tickpulse` dotnet tool; env TICKPULSE_API_URL, TICKPULSE_API_KEY

docker compose up                                          # DB + API + dashboard, migrations, seed
docker compose --profile observability up                  # plus collector, Loki, Tempo, Prometheus, Grafana
docker compose run cli avg BTCUSDT

npm --prefix web ci
npm --prefix web run dev | build | lint | typecheck
```

## Conventions

- **Money is `decimal`** in C# and `numeric(24,8)` (price) / `numeric(28,8)` (quantity) in SQL.
  Never `double`/`float`. Final averages round to 8 dp with `MidpointRounding.ToEven`.
- **Clock**: inject `TimeProvider`; never `DateTime.UtcNow`/`DateTimeOffset.UtcNow`.
  Tests use `FakeTimeProvider`.
- **DTO timestamps** are `DateTime` with `Kind=Utc` (not `DateTimeOffset`, which
  `DataContractSerializer` renders as a complex type). JSON is ISO-8601 with `Z`.
- **Symbols** are case-insensitive: trimmed, checked against `^[A-Za-z0-9]{5,20}$` (ASCII), then
  upper-cased (`SymbolName.TryCreate`).
- **Week buckets** start Monday 00:00 UTC, origin `2000-01-03T00:00:00Z`, identical in C# and
  SQL (`time_bucket(width, ts, origin)`).
- **Errors** are RFC 9457 ProblemDetails, negotiated as JSON or XML on every path. Type URIs are
  `https://tickpulse.dev/problems/{slug}` with slugs `unknown-symbol`, `no-data`,
  `invalid-symbol`, `symbol-limit`, `admin-disabled`, `validation`, `not-acceptable`,
  `rate-limited`, `internal`, `not-found` (unmatched routes), `unauthorized` (401: missing or wrong
  `X-Api-Key`), `upstream-unavailable` (503: Binance cannot confirm a symbol being added right now),
  `method-not-allowed` (405, with `Allow`), `unsupported-media-type` (415: a request body that is
  neither JSON nor XML). `HEAD` works wherever `GET` does.
- **Config keys** (env uses `__`): `ConnectionStrings:TickPulse`, `Admin:*`, `Binance:*`,
  `Ingestion:*`, `Backfill:*`, `Database:RunMigrationsOnStartup`, `Otel:Endpoint`,
  `Otel:FlushSampleRatio`.
  Defaults are listed in the plan's Global Constraints, plus two leader-election keys:
  `Ingestion:LeaderRetryInterval` (`00:00:10`, how often a standby instance tries the lock) and
  `Ingestion:LeaderCheckInterval` (`00:00:05`, how often the leader probes it).
  API host keys: `RateLimiting:PermitLimit` (`300`) and `RateLimiting:WindowSeconds` (`60`), a
  fixed window per client (IPv4 address, IPv6 /64) on the API controllers;
  `ForwardedHeaders:KnownProxies` (IPs) and `ForwardedHeaders:KnownNetworks` (CIDRs, e.g. the
  Docker bridge `172.16.0.0/12`), each an array or comma-separated and added to loopback: the
  proxies whose `CF-Connecting-IP` is trusted as the client IP (`X-Forwarded-For` is never read);
  `Otel:IpSalt`, the salt of the logged client-IP hash (random per process when unset);
  `Otel:SampledRequestsPerSecond` (`10`, see Telemetry).
  Hub and stats keys: `MarketHub:ConnectPermitLimit` (`60`) and `MarketHub:ConnectWindowSeconds`
  (`60`), hub connection attempts per client IP; `SystemStats:CacheTtl` (`00:00:02`) and
  `SystemStats:ReadTimeout` (`00:00:01`), the cache and read timeout of the stored-row counts.
  The README's configuration table lists every key with its default; keep it in step when adding one.
- **Telemetry**: the `TickPulse` meter and activity source live in
  `Infrastructure/Telemetry/TickPulseMetrics.cs`; their names, units and tags are a contract with
  the dashboards and alerts (`deploy/observability/README.md`). Record per batch, never per tick.
  `Otel:FlushSampleRatio` (default 0.1) is the share of `ingest.flush` traces kept; backfill pages
  and gap-fills are always traced, other parentless spans never.
  Requests are sampled at the source (`RequestSampleBudget`, `RequestLogPolicy`): a token bucket of
  `Otel:SampledRequestsPerSecond` (default 10) decides which ordinary requests keep their trace and
  Information log line; the rest are Debug and counted only by the metrics. Unhandled exceptions and
  5xx outside `/health` log at Error, `/health` 503 at Warning, 4xx and requests slower than 250 ms at
  Information, always. **429 is the exception**: it is sampled like a 2xx, because rejections arrive
  in floods (ADR 0016). Every request logged at Information or above keeps its trace.
  Logging is Serilog only (JSON console, plus OTLP when `Otel:Endpoint` is set; empty = off); levels
  live under `Serilog:MinimumLevel`. Never log raw IPs, keys or secrets: use `client_ip_hash`.
- **Error negotiation**: problems go through `IProblemDetailsService` with two writers, XML
  (`application/problem+xml`) when `Accept` prefers XML, otherwise JSON. Higher q wins; at equal q
  a concrete type beats a wildcard, and between concrete types the first listed wins. The host
  reorders `Accept` by q, then specificity, before MVC, so successes follow the same rule.
  Controllers return errors as `ProblemResult(ProblemFactory.Create(...))`, so an error is never
  replaced by a 406: a client that accepts neither format gets the problem as JSON. Only a
  successful response can be a 406. `/api` responses carry `Vary: Accept`.
- **Startup migrations** retry transient Npgsql errors (5 attempts, 1/2/4/8 s backoff, 2 min in
  total); beyond that startup fails and the container restart policy takes over.
- **Clearing tables**: in SQL and shell scripts, clear tables with `DELETE FROM`.
- **Packages**: versions live only in `Directory.Packages.props` (Central Package Management).
  Add a `PackageReference` only to the project that needs it.
- **Build settings** (`Directory.Build.props`): `net10.0`, `Nullable`, `ImplicitUsings`,
  `TreatWarningsAsErrors`, `InvariantGlobalization`, `AnalysisLevel=latest-recommended` (CA
  rules such as CA1822/CA1001 are build errors). Fix them; do not suppress them broadly.
- **Commits**: scoped Conventional Commits, e.g. `feat(domain): …`, `test(api): …`,
  `fix(ingestion): …`. Never `--no-verify`.

## Workflow

Every change goes through one worktree under `.claude/worktrees/`, a `feat|fix|chore|docs/<slug>`
branch from the latest `origin/main`, and a PR to `main` with CI (`backend`) green. No direct
commits to `main`. Full procedure: skill `tickpulse-pr-workflow`.

## Definition of done

1. Tests written first for Domain logic (TDD); integration tests hit a real TimescaleDB via
   Testcontainers. See skill `tickpulse-testing`.
2. `dotnet format --verify-no-changes`, `dotnet build -c Release` and `dotnet test` are green
   locally — you saw them pass. Web changes: `lint`, `typecheck` and `build` green too.
3. Coverage gates hold: `TickPulse.Domain` ≥ 95 % lines, overall ≥ 80 % lines.
4. Architecture tests pass; no new cross-layer references.
5. Docs and ADRs updated when behaviour, config or a design decision changes.
6. PR opened against `main` using the template (Summary / Changes / Tests run / Risks).

## Pointers

- Design: `docs/superpowers/specs/2026-11-03-tickpulse-design.md` (binding; §4 decisions, §5
  architecture, §6 testing, §8 CI/CD)
- Plan: `docs/superpowers/plans/2026-11-03-tickpulse.md` (Global Constraints, task order)
- Skills: `.claude/skills/tickpulse-pr-workflow`, `.claude/skills/tickpulse-architecture`,
  `.claude/skills/tickpulse-testing`
- ADRs: `docs/adr/` (index in `docs/adr/README.md`; add a record for every new design decision)
- Deployment and scale-out design: `docs/deployment.md` · Defects log: `docs/ai/defects-log.md`
  (add an entry for every real defect a review or live check finds) · Engineering notes:
  `engineering-notes.md`
