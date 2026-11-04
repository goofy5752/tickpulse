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
- **Symbols** are case-insensitive, normalised to upper case, regex `^[A-Z0-9]{5,20}$`.
- **Week buckets** start Monday 00:00 UTC, origin `2000-01-03T00:00:00Z`, identical in C# and
  SQL (`time_bucket(width, ts, origin)`).
- **Errors** are RFC 9457 ProblemDetails, negotiated as JSON or XML on every path. Type URIs are
  `https://tickpulse.dev/problems/{slug}` with slugs `unknown-symbol`, `no-data`,
  `invalid-symbol`, `symbol-limit`, `admin-disabled`, `validation`, `not-acceptable`,
  `rate-limited`, `internal`.
- **Config keys** (env uses `__`): `ConnectionStrings:TickPulse`, `Admin:*`, `Binance:*`,
  `Ingestion:*`, `Backfill:*`, `Database:RunMigrationsOnStartup`, `Otel:Endpoint`.
  Defaults are listed in the plan's Global Constraints, plus two leader-election keys:
  `Ingestion:LeaderRetryInterval` (`00:00:10`, how often a standby instance tries the lock) and
  `Ingestion:LeaderCheckInterval` (`00:00:05`, how often the leader probes it).
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
- ADRs: `docs/adr/` · Deployment: `docs/deployment.md` · Defects log: `docs/ai/defects-log.md`
