# TickPulse Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task by task. Steps use checkbox (`- [ ]`) syntax for tracking. Before you start, read `CLAUDE.md`, `CLAUDE.local.md` (if present) and the spec.

**Goal:** Build TickPulse. It ingests Binance spot trades over WebSocket into PostgreSQL/TimescaleDB, serves the 24h average price and SMA over a JSON/XML HTTP API, lets admins manage symbols at runtime, and ships with a console client, a live dashboard and full observability. It is deployed by CI/CD to `tickpulse.dev`.

**Architecture:** One .NET 10 process: an ASP.NET Core API plus hosted ingestion and backfill workers. The layers are Domain ← Application ← Infrastructure, and Api is the composition root. Ticks flow WS → bounded Channel → batched `COPY` + idempotent insert. The same SQL statement upserts the 1-minute candles. All analytics read from SQL. A React dashboard is served as static files by the API. Telemetry goes out over OTLP to a collector → Loki/Tempo/Prometheus → Grafana.

**Tech Stack:**
- Backend: .NET 10, ASP.NET Core MVC, EF Core 10 + Npgsql, TimescaleDB (PG17)
- Tests: xUnit v3, Shouldly, NSubstitute, Testcontainers, NetArchTest
- CLI: System.CommandLine + Spectre.Console
- Logging and telemetry: Serilog + OpenTelemetry; Grafana, Loki, Tempo, Prometheus, OTel Collector
- Web: React 19, Vite, TypeScript, Tailwind 4, shadcn/ui, TanStack Query, lightweight-charts, @microsoft/signalr
- Delivery: Docker, GitHub Actions, GHCR

**Spec:** `docs/superpowers/specs/2026-11-03-tickpulse-design.md`. Section numbers like §4.5 refer to it. Read it before any task.

## Global Constraints

- Target `net10.0`. `global.json` pins SDK `10.0.204` with `rollForward: latestFeature`. Central Package Management (`Directory.Packages.props`). `Nullable=enable`, `TreatWarningsAsErrors=true`, `ImplicitUsings=enable`, `InvariantGlobalization=true`.
- Prices and quantities are `decimal` in C# and `numeric(24,8)`/`numeric(28,8)` in SQL. **Never `double`/`float`** for money. Final computed averages are rounded to **8 dp**, `MidpointRounding.ToEven`.
- All clock reads go through `TimeProvider` (inject it; never `DateTime.UtcNow`).
- DTO timestamps are `DateTime` with `Kind=Utc`, never `DateTimeOffset`, because `DataContractSerializer` renders `DateTimeOffset` as a complex type in XML. JSON uses ISO-8601 with `Z`.
- The public routes are fixed: `GET /api/{symbol}/24hAvgPrice` and `GET /api/{symbol}/SimpleMovingAverage?n=&p=&s=` with `p ∈ {1w,1d,30m,5m,1m}`. Symbols are case-insensitive and normalised to upper case.
- Initial symbols are seeded by migration: `BTCUSDT`, `ADAUSDT`, `ETHUSDT`.
- Problem types use the base URI `https://tickpulse.dev/problems/` with the slugs `unknown-symbol`, `no-data`, `invalid-symbol`, `symbol-limit`, `admin-disabled`, `validation`, `not-acceptable`, `rate-limited`, `internal`.
- Week buckets are aligned to **Monday 00:00 UTC** with origin `2000-01-03T00:00:00Z`. Use the same origin in C# and SQL (`time_bucket(width, ts, origin)`).
- Config keys (env uses `__`):
  - `ConnectionStrings:TickPulse`
  - `Admin:ApiKey`, `Admin:MaxActiveSymbols` (20)
  - `Binance:WsBaseUrl` (`wss://stream.binance.com:9443`), `Binance:RestBaseUrl` (`https://api.binance.com`)
  - `Ingestion:RotationInterval` (`23:00:00`), `Ingestion:BatchMaxSize` (5000), `Ingestion:BatchMaxDelay` (`00:00:00.500`), `Ingestion:ChannelCapacity` (100000), `Ingestion:ReconcileInterval` (`00:00:30`)
  - `Backfill:Enabled` (false in `appsettings.json`), `Backfill:Depth` (`182.00:00:00`)
  - `Database:RunMigrationsOnStartup` (true)
  - `Otel:Endpoint` (unset = exporters off)
- Repo rules from `CLAUDE.local.md` (if present) are binding.
- In shell commands and SQL written by agents, use `DELETE FROM` rather than the T-word for clearing tables (a local safety hook blocks it).
- Toolchain on this Windows machine:
  - `docker` lives at `C:\Program Files\Docker\Docker\resources\bin` and `gh` at `C:\Program Files\GitHub CLI`. Prepend them to `PATH` if a shell doesn't find them.
  - Node is at `C:\Program Files\nodejs`.
  - Docker Desktop must be running for Testcontainers.

## Review Focus

1. **Replayed ticks after a reconnect, rotation overlap or gap-fill must not double-count.** Candle `price_sum`/`tick_count` must stay exact, so `24hAvgPrice` equals a raw `AVG(price)`. Pinned in Task 7 (`WriteBatch_Twice_LeavesCandlesUnchanged`) and Task 8 (`Average_EqualsRawAvg_WithDuplicatesReplayed`).
2. **Messy symbol input** (`btcusdt`, ` ETHUSDT `, `BTC-USDT`, `ADMIN`) must normalise or be rejected cleanly: 404 `unknown-symbol` or 422 `invalid-symbol`, never a 500. Pinned in Task 14 (`Avg_LowercaseSymbol_Works`, `Avg_GarbageSymbol_Returns404Problem`) and Task 15 (`Add_InvalidFormat_Returns422`).
3. **`s` edge cases:** exactly on a bucket boundary, in the future, with no offset, or with a `+03:00` offset. Each must give a correctly aligned window or a 400. Pinned in Task 3 and Task 14 (`Sma_SWithOffset_IsConvertedToUtc`, `Sma_FutureS_Returns400`).
4. **Real-world `Accept` headers:**
   - the browser default `text/html,…,application/xml;q=0.9,*/*;q=0.8` → XML
   - `*/*` and a missing header → JSON
   - `application/json;q=0.5, application/xml` → XML
   - `text/csv` → 406 with a problem body
   - all of the above also on error responses

   Pinned in Task 13 (`Negotiation_Matrix`).
5. **Symbol churn during reconnects:** removing and re-adding a symbol, or adding one while the socket is reconnecting, must end up subscribed exactly once. Pinned in Task 11 (`AddDuringReconnect_EventuallySubscribed`, `RemoveThenReadd_SubscribedOnce`).

---

## File Structure

```
tickpulse/
  global.json · Directory.Build.props · Directory.Packages.props · .editorconfig · TickPulse.slnx
  CLAUDE.md                         # committed agent context (neutral)
  CLAUDE.local.md                   # git-ignored local overrides
  .claude/skills/{tickpulse-pr-workflow,tickpulse-architecture,tickpulse-testing}/SKILL.md
  .github/workflows/ci.yml · .github/dependabot.yml · .github/pull_request_template.md
  Dockerfile · docker-compose.yml · .env.example
  deploy/
    docker-compose.prod.yml · smoke.sh · nginx/tickpulse.conf · nginx/grafana.tickpulse.conf
    observability/{otel-collector.yaml,prometheus.yml,loki.yaml,tempo.yaml}
    observability/grafana/{provisioning/datasources/*.yaml,provisioning/dashboards/*.yaml,provisioning/alerting/*.yaml,dashboards/*.json}
  src/
    TickPulse.Domain/        Period.cs · SymbolName.cs · SmaWindow.cs · SmaCalculator.cs · PricePoint.cs · AverageCalculator.cs · Tick.cs · Kline.cs
    TickPulse.Application/   Abstractions/*.cs · Analytics/AnalyticsService.cs · Symbols/SymbolAdminService.cs · Results/AppResult.cs · Ingestion/IngestionState.cs
    TickPulse.Infrastructure/
      Persistence/  TickPulseDbContext.cs · SymbolEntity.cs · Migrations/* · SymbolRepository.cs · TickWriter.cs · CandleWriter.cs · MarketDataQueries.cs · PostgresLeaderLock.cs
      Binance/      BinanceRestClient.cs · BinanceWebSocketConnection.cs · StreamMessageParser.cs · SubscribeThrottle.cs
      Ingestion/    IngestionWorker.cs · TickBatchWriter.cs · SymbolReconciler.cs · GapFiller.cs · ReconnectBackoff.cs
      Backfill/     BackfillWorker.cs
      Telemetry/    TickPulseMetrics.cs
      DependencyInjection.cs
    TickPulse.Contracts/     *.cs (DTOs)
    TickPulse.Api/           Program.cs · Controllers/{AnalyticsController,SymbolsController,AdminSymbolsController,SystemController}.cs
                             Problems/{XmlProblemDetailsWriter,ProblemFactory}.cs · Auth/ApiKeyAuthenticationHandler.cs
                             Hubs/{MarketHub,MarketBroadcaster}.cs · Telemetry/{ApiMetricsMiddleware,ClientIpHasher}.cs · wwwroot/ (built web)
    TickPulse.Cli/           Program.cs · TickPulseApiClient.cs · Commands/*.cs · Rendering/*.cs
  tests/
    TickPulse.Domain.Tests · TickPulse.Application.Tests · TickPulse.Integration.Tests (Fixtures/{TimescaleFixture,FakeBinanceServer,ApiFactory}.cs) · TickPulse.Cli.Tests · TickPulse.Architecture.Tests
  web/  package.json · vite.config.ts · src/{main.tsx,App.tsx,theme.css,api/*,hooks/*,sections/*,components/ui/*}
  docs/ adr/ · deployment.md · ai/{prompts.md,defects-log.md} · superpowers/{specs,plans}/
  README.md · engineering-notes.md
```

---

## Phase 0 — Bootstrap

### Task 1: Repository scaffold, agent context, CI skeleton

**Files:**
- Create: all root config files listed above, the 9 projects (5 src + 5 tests, minus web), `CLAUDE.md`, the 3 skills, `.github/*`
- Test: `tests/TickPulse.Architecture.Tests/LayeringTests.cs`

**Interfaces:**
- Produces: the project references `Domain ← Application ← Infrastructure ← Api`, `Contracts ← Api`, `Contracts ← Cli`. Domain references nothing. Application references only Domain. The CLI references only Contracts.

- [ ] **Step 1: Scaffold the solution.** `dotnet new sln --format slnx -n TickPulse`, then `classlib` for Domain/Application/Infrastructure/Contracts, `webapi --use-controllers` for Api, `console` for Cli, and `xunit3` (or `classlib` + xunit.v3 packages) for the tests. Wire the references as in Interfaces. In `Directory.Packages.props`, pin the latest stable versions of every package named in Tech Stack, plus `coverlet.collector` and `Microsoft.AspNetCore.Mvc.Testing`.
- [ ] **Step 2: Write the failing architecture test**

```csharp
[Fact] public void Domain_HasNoDependencies() =>
    Types.InAssembly(typeof(TickPulse.Domain.Period).Assembly)
      .ShouldNot().HaveDependencyOnAny("TickPulse.Application","TickPulse.Infrastructure","TickPulse.Api","Microsoft.EntityFrameworkCore","Npgsql")
      .GetResult().IsSuccessful.ShouldBeTrue();
[Fact] public void Application_DoesNotDependOnInfrastructureOrApi() => /* same pattern */;
[Fact] public void Cli_DependsOnlyOnContracts() => /* Cli must not reference Domain/Application/Infrastructure */;
```

- [ ] **Step 3: Add a placeholder `Period` type** so the test compiles. Run `dotnet test tests/TickPulse.Architecture.Tests`. Expected: PASS.
- [ ] **Step 4: Write `CLAUDE.md`** (≤ 120 lines). It covers what TickPulse is, the layer rules, the commands (build/test/run/compose), the conventions (decimal, TimeProvider, UTC DTOs, ProblemDetails slugs, DELETE-not-T-word), the definition of done, and pointers to the spec, plan and skills.
- [ ] **Step 5: Write the three skills.**
  - `tickpulse-pr-workflow` is adapted from the owner's existing workflow skill. It covers one worktree per change under `.claude/worktrees/`, branches `feat|fix|chore|docs/<slug>` from the latest `origin/main`, scoped Conventional Commits, re-syncing before push, local green before PR, `gh pr create --base main`, and stopping at "PR opened" unless told to merge.
  - `tickpulse-architecture` holds the layer rules, where things go, and the ingestion invariants (idempotency, single-statement candle upsert, leader lock).
  - `tickpulse-testing` sets TDD for Domain, says Testcontainers is always used (never the in-memory provider), lists the fixtures, sets the coverage gates (Domain ≥ 95% lines, overall ≥ 80%), and requires naming tests `Method_Condition_Expected`.
- [ ] **Step 6: Write `.github/workflows/ci.yml`** with a `backend` job:
  1. Run on `ubuntu-latest`.
  2. `actions/setup-dotnet` with the version from `global.json`.
  3. `dotnet restore`, then `dotnet format --verify-no-changes`, then `dotnet build -c Release --no-restore`.
  4. `dotnet test -c Release --collect "XPlat Code Coverage"`.
  5. ReportGenerator writes a Markdown summary into `$GITHUB_STEP_SUMMARY`. A threshold step fails the job if the Domain line coverage is < 95 or the overall coverage is < 80.

  Also add `.github/dependabot.yml` (nuget, npm `/web`, github-actions, docker; weekly) and `.github/pull_request_template.md` (Summary / Changes / Tests run / Risks).
- [ ] **Step 7: Make sure the ignores are right.** `.gitignore` already has `.superpowers/` and `CLAUDE.local.md`. Add `.claude/worktrees/`, `coverage/`, `web/dist/` and `src/TickPulse.Api/wwwroot/`.
- [ ] **Step 8: Bootstrap commit and repo** (the only direct commit to `main`):

```bash
git add -A && git commit -m "chore: bootstrap solution, agent context and CI"
gh repo create goofy5752/tickpulse --public --source . --remote origin --push
gh api -X PUT repos/goofy5752/tickpulse/branches/main/protection --input - <<'JSON'
{"required_status_checks":{"strict":true,"contexts":["backend"]},"enforce_admins":false,"required_pull_request_reviews":null,"restrictions":null}
JSON
```
Expected: the repo exists, CI goes green on `main`, and protection is set. Add the `web` check to the required contexts in Task 21.

---

## Phase 1 — Domain (TDD, no I/O)

### Task 2: `Period` and bucket alignment

**Files:** Create `src/TickPulse.Domain/Period.cs`. Test `tests/TickPulse.Domain.Tests/PeriodTests.cs`

**Interfaces:**
- Produces: `public readonly record struct Period` with `string Code`, `TimeSpan Length`, `static Period OneMinute|FiveMinutes|ThirtyMinutes|OneDay|OneWeek`, `static IReadOnlyList<Period> All`, `static bool TryParse(string? code, out Period period)` (exact, case-sensitive codes `1m,5m,30m,1d,1w`), `DateTime AlignDown(DateTime utc)`, `DateTime AlignUp(DateTime utc)`, and `static readonly DateTime Origin = 2000-01-03T00:00:00Z`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Theory]
[InlineData("1m",60)] [InlineData("5m",300)] [InlineData("30m",1800)] [InlineData("1d",86400)] [InlineData("1w",604800)]
public void TryParse_ValidCode_ReturnsLength(string code,int seconds){ Period.TryParse(code,out var p).ShouldBeTrue(); p.Length.TotalSeconds.ShouldBe(seconds); }
[Theory] [InlineData("1h")] [InlineData("1M")] [InlineData("")] [InlineData(null)] [InlineData(" 1d")]
public void TryParse_InvalidCode_ReturnsFalse(string? code)=>Period.TryParse(code,out _).ShouldBeFalse();
[Fact] public void AlignDown_Week_IsMondayMidnightUtc() =>
  Period.OneWeek.AlignDown(Utc("2026-11-05T13:45:00")).ShouldBe(Utc("2026-11-02T00:00:00")); // Thu -> Mon
[Fact] public void AlignDown_OnBoundary_IsIdentity() => Period.FiveMinutes.AlignDown(Utc("2026-11-03T10:05:00")).ShouldBe(Utc("2026-11-03T10:05:00"));
[Fact] public void AlignUp_OnBoundary_IsIdentity() => Period.OneDay.AlignUp(Utc("2026-11-03T00:00:00")).ShouldBe(Utc("2026-11-03T00:00:00"));
[Fact] public void AlignUp_OffBoundary_GoesToNext() => Period.ThirtyMinutes.AlignUp(Utc("2026-11-03T10:00:01")).ShouldBe(Utc("2026-11-03T10:30:00"));
[Fact] public void Align_NonUtcKind_Throws() => Should.Throw<ArgumentException>(()=>Period.OneMinute.AlignDown(new DateTime(2026,1,1,0,0,0,DateTimeKind.Local)));
```

- [ ] **Step 2: Run** `dotnet test tests/TickPulse.Domain.Tests --filter PeriodTests`. Expected: FAIL (the members don't exist yet).
- [ ] **Step 3: Implement `Period`.** Alignment is `Origin + floor((t − Origin) / Length) × Length`, using ticks arithmetic. `AlignUp` is `AlignDown(t) == t ? t : AlignDown(t) + Length`.
- [ ] **Step 4: Run.** Expected: PASS.
- [ ] **Step 5: Commit** `feat(domain): period codes and UTC bucket alignment`

### Task 3: SMA window and calculator

**Files:** Create `src/TickPulse.Domain/{SmaWindow.cs,SmaCalculator.cs,PricePoint.cs}`. Test `tests/TickPulse.Domain.Tests/SmaTests.cs`

**Interfaces:**
- Consumes: `Period` (Task 2).
- Produces:
  - `public readonly record struct PricePoint(DateTime BucketStart, decimal Close)`
  - `public readonly record struct SmaWindow(DateTime Start, DateTime EndExclusive) { bool IsEmpty => Start >= EndExclusive; }`
  - `public static class SmaWindowCalculator { static SmaWindow Compute(Period p, int n, DateTime? startUtc, DateTime nowUtc); }`
  - `public static class SmaCalculator { static decimal? Average(IReadOnlyList<PricePoint> points); }` returns null when the list is empty, otherwise rounds to 8 dp ToEven.

The window rules come from spec §4.5:
- `s` omitted: `End = p.AlignDown(now)` and `Start = End − n×Length`.
- `s` given: `Start = p.AlignUp(s)` and `End = min(Start + n×Length, p.AlignDown(now))`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void Window_NoS_EndsAtLastCompleteBucket() {
  var w = SmaWindowCalculator.Compute(Period.OneDay, 3, null, Utc("2026-11-03T15:00:00"));
  w.ShouldBe(new SmaWindow(Utc("2026-10-31T00:00:00"), Utc("2026-11-03T00:00:00"))); }
[Fact] public void Window_WithS_StartsAtFirstBucketAtOrAfterS() {
  var w = SmaWindowCalculator.Compute(Period.OneDay, 2, Utc("2026-10-01T06:00:00"), Utc("2026-11-03T15:00:00"));
  w.ShouldBe(new SmaWindow(Utc("2026-10-02T00:00:00"), Utc("2026-10-04T00:00:00"))); }
[Fact] public void Window_WithSOnBoundary_StartsAtS() => SmaWindowCalculator.Compute(Period.OneDay,1,Utc("2026-10-01T00:00:00"),Utc("2026-11-03T00:00:00")).Start.ShouldBe(Utc("2026-10-01T00:00:00"));
[Fact] public void Window_WithS_ClippedAtNow() =>
  SmaWindowCalculator.Compute(Period.OneDay, 10, Utc("2026-11-01T00:00:00"), Utc("2026-11-03T15:00:00")).EndExclusive.ShouldBe(Utc("2026-11-03T00:00:00"));
[Fact] public void Window_SInCurrentBucket_IsEmpty() =>
  SmaWindowCalculator.Compute(Period.OneDay, 5, Utc("2026-11-03T01:00:00"), Utc("2026-11-03T15:00:00")).IsEmpty.ShouldBeTrue();
[Fact] public void Window_Week_UsesMondayBuckets() =>
  SmaWindowCalculator.Compute(Period.OneWeek, 2, null, Utc("2026-11-05T00:00:00")).ShouldBe(new SmaWindow(Utc("2026-10-19T00:00:00"), Utc("2026-11-02T00:00:00")));
[Fact] public void Average_Empty_IsNull() => SmaCalculator.Average([]).ShouldBeNull();
[Fact] public void Average_RoundsTo8dpBankers() => SmaCalculator.Average([P(0.000000015m), P(0.000000015m)]).ShouldBe(0.00000002m);
[Fact] public void Average_TinyPrices_NoPrecisionLoss() => SmaCalculator.Average([P(0.00001234m), P(0.00001236m)]).ShouldBe(0.00001235m);
```

- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Implement.** Validate `n ≥ 1` (ArgumentOutOfRange) and the UTC kinds.
- [ ] **Step 4: Run.** Expected: PASS.
- [ ] **Step 5: Commit** `feat(domain): SMA window semantics and calculator`

### Task 4: Ticks, klines, average math, symbol names

**Files:** Create `src/TickPulse.Domain/{Tick.cs,Kline.cs,AverageCalculator.cs,SymbolName.cs}`. Test `tests/TickPulse.Domain.Tests/{AverageCalculatorTests,SymbolNameTests}.cs`

**Interfaces:**
- Produces:
  - `public sealed record Tick(int SymbolId, long AggTradeId, decimal Price, decimal Quantity, DateTime TradeTimeUtc, DateTime ReceivedAtUtc)`
  - `public sealed record Kline(DateTime OpenTimeUtc, decimal Open, decimal High, decimal Low, decimal Close, decimal Volume, decimal QuoteVolume)`
  - `public readonly record struct PriceSumPart(decimal Sum, long Count)`
  - `public static class AverageCalculator { static decimal? Combine(IEnumerable<PriceSumPart> parts); }`, which returns ΣSum/ΣCount rounded to 8 dp, or null if the count is 0
  - `public readonly record struct SymbolName`, with `string Value` and `static bool TryCreate(string? raw, out SymbolName name)`. It trims, upper-cases (invariant) and matches `^[A-Z0-9]{5,20}$`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void Combine_WeightsByCount() => AverageCalculator.Combine([new(300m,3), new(10m,1)]).ShouldBe(77.5m);
[Fact] public void Combine_NoSamples_IsNull() => AverageCalculator.Combine([new(0m,0)]).ShouldBeNull();
[Theory] [InlineData("btcusdt","BTCUSDT")] [InlineData("  ETHUSDT ","ETHUSDT")]
public void TryCreate_Normalises(string raw,string expected){ SymbolName.TryCreate(raw,out var s).ShouldBeTrue(); s.Value.ShouldBe(expected); }
[Theory] [InlineData("BTC-USDT")] [InlineData("BTC")] [InlineData("")] [InlineData(null)] [InlineData("ABCDEFGHIJKLMNOPQRSTU")]
public void TryCreate_Invalid_ReturnsFalse(string? raw)=>SymbolName.TryCreate(raw,out _).ShouldBeFalse();
```

- [ ] **Step 2: Run.** Expected: FAIL. **Step 3: Implement.** **Step 4: Run.** Expected: PASS, and the Domain coverage is ≥ 95% (`dotnet test tests/TickPulse.Domain.Tests --collect "XPlat Code Coverage"`).
- [ ] **Step 5: Commit** `feat(domain): tick, kline, average combination and symbol names`, then open a PR for Tasks 2–4 (`feat: domain model`).

---

## Phase 2 — Persistence

### Task 5: Application abstractions and results

**Files:** Create `src/TickPulse.Application/Abstractions/*.cs` and `Results/AppResult.cs`. Test `tests/TickPulse.Application.Tests/AppResultTests.cs`

**Interfaces (produced; used by every later task):**

```csharp
public enum AppError { UnknownSymbol, NoData, InvalidSymbol, SymbolLimit, Validation }
public sealed record AppResult<T>(T? Value, AppError? Error, string? Detail) { bool IsSuccess; static AppResult<T> Ok(T v); static AppResult<T> Fail(AppError e, string detail); }
public sealed record MonitoredSymbol(int Id, string Name, bool IsActive, DateTime CreatedAtUtc, DateTime? DeactivatedAtUtc, BackfillStatus BackfillStatus, DateTime? BackfilledFromUtc);
public enum BackfillStatus { NotRequested, Pending, Running, Completed, Failed }
public interface ISymbolRepository {
  Task<IReadOnlyList<MonitoredSymbol>> ListAsync(CancellationToken ct);
  Task<IReadOnlyList<MonitoredSymbol>> ListActiveAsync(CancellationToken ct);
  Task<MonitoredSymbol?> FindAsync(SymbolName name, CancellationToken ct);
  Task<MonitoredSymbol> AddOrReactivateAsync(SymbolName name, CancellationToken ct);
  Task<bool> DeactivateAsync(SymbolName name, CancellationToken ct);
  Task<int> CountActiveAsync(CancellationToken ct);
  Task SetBackfillStatusAsync(int symbolId, BackfillStatus status, DateTime? fromUtc, CancellationToken ct); }
public interface ITickWriter { Task<int> WriteBatchAsync(IReadOnlyList<Tick> ticks, CancellationToken ct); } // returns newly inserted count
public interface ICandleWriter { Task<int> InsertBackfillAsync(int symbolId, IReadOnlyList<Kline> klines, CancellationToken ct); }
public sealed record AverageWindow(decimal Average, DateTime FirstTickUtc, DateTime LastTickUtc, long SampleCount);
public sealed record CandleRow(DateTime BucketStart, decimal Open, decimal High, decimal Low, decimal Close, decimal Volume);
public interface IMarketDataQueries {
  Task<AverageWindow?> GetAverageAsync(int symbolId, DateTime fromUtc, DateTime toUtc, CancellationToken ct);
  Task<IReadOnlyList<PricePoint>> GetClosesAsync(int symbolId, Period p, SmaWindow w, CancellationToken ct);
  Task<IReadOnlyList<CandleRow>> GetCandlesAsync(int symbolId, Period p, int limit, CancellationToken ct);
  Task<decimal?> GetCloseAtOrBeforeAsync(int symbolId, DateTime utc, CancellationToken ct);
  Task<long> ApproximateTickCountAsync(CancellationToken ct);
  Task<DateTime?> FirstStreamMinuteAsync(int symbolId, CancellationToken ct); }
public interface IExchangeMetadata { Task<bool> IsTradingSymbolAsync(SymbolName name, CancellationToken ct); }
public interface IKlineSource { Task<IReadOnlyList<Kline>> GetMinuteKlinesAsync(SymbolName s, DateTime fromUtc, DateTime toUtc, int limit, CancellationToken ct); }
public interface IAggTradeSource { Task<IReadOnlyList<(long Id, decimal Price, decimal Qty, DateTime TimeUtc)>> GetAggTradesFromAsync(SymbolName s, long fromId, int limit, CancellationToken ct); }
public interface ISymbolChangeSignal { void Notify(); ValueTask WaitAsync(CancellationToken ct); }
public interface IBackfillQueue { void Enqueue(int symbolId); }
```

- [ ] **Step 1:** Test `Ok_IsSuccess` and `Fail_CarriesErrorAndDetail`. **Step 2:** run → FAIL. **Step 3:** implement. **Step 4:** run → PASS. **Step 5:** commit `feat(app): abstractions and result type`.

### Task 6: Schema, migrations, seed, Testcontainers fixture

**Files:**
- Create `src/TickPulse.Infrastructure/Persistence/{TickPulseDbContext.cs,SymbolEntity.cs,Migrations/*}` and `tests/TickPulse.Integration.Tests/Fixtures/TimescaleFixture.cs`
- Test `tests/TickPulse.Integration.Tests/Persistence/SchemaTests.cs`

**Interfaces:**
- Produces: `TimescaleFixture` (an xUnit assembly fixture) exposing `string ConnectionString` and `Task ResetAsync()`. `ResetAsync` runs `DELETE FROM` over the ticks/candles and re-seeds the symbols.
- Produces the tables below (snake_case via `UseSnakeCaseNamingConvention`):
  - `symbols(id serial PK, name varchar(20) UNIQUE NOT NULL, is_active bool, created_at timestamptz, updated_at timestamptz, deactivated_at timestamptz NULL, backfill_status smallint, backfilled_from timestamptz NULL)`
  - `price_ticks(symbol_id int FK, agg_trade_id bigint, price numeric(24,8), quantity numeric(28,8), trade_time timestamptz, ingested_at timestamptz, PRIMARY KEY(symbol_id, agg_trade_id, trade_time))`, a hypertable on `trade_time` with a 1-day chunk interval, an index `(symbol_id, trade_time DESC)`, compression `segmentby symbol_id, orderby trade_time` after 2 days, and retention 14 days
  - `candles_1m(symbol_id int, bucket_start timestamptz, open/high/low/close numeric(24,8), first_trade_id bigint NULL, last_trade_id bigint NULL, price_sum numeric(38,8) NULL, tick_count bigint NULL, volume numeric(28,8), quote_volume numeric(38,8), source smallint /*1=stream,2=backfill*/, PRIMARY KEY(symbol_id,bucket_start))`
  - the `tick_staging` UNLOGGED table, shaped like `price_ticks` without constraints

  EF maps only `symbols`. The Timescale DDL lives in `migrationBuilder.Sql(...)`.

- [ ] **Step 1: Write the failing tests:**
  - `Migrations_CreateHypertable`: `timescaledb_information.hypertables` contains `price_ticks`
  - `Migrations_SeedInitialSymbols`: the names are exactly `{ADAUSDT, BTCUSDT, ETHUSDT}`, all active
  - `Migrations_RegisterCompressionAndRetentionJobs`: `timescaledb_information.jobs` has `policy_compression` and `policy_retention` for `price_ticks`
- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Implement** the DbContext, the `Initial` migration and the fixture. The fixture uses `Testcontainers.PostgreSql` with the image pinned to the newest `timescale/timescaledb:2.x.y-pg17` tag (the same tag goes into the compose files) and calls `Database.MigrateAsync()` once.
- [ ] **Step 4: Run** with Docker running. Expected: PASS.
- [ ] **Step 5: Commit** `feat(infra): schema, timescale policies, seed and test fixture`

### Task 7: Tick writer with idempotent candle upsert

**Files:** Create `src/TickPulse.Infrastructure/Persistence/{TickWriter.cs,CandleWriter.cs,SymbolRepository.cs}`. Test `tests/TickPulse.Integration.Tests/Persistence/TickWriterTests.cs` and `SymbolRepositoryTests.cs`

**Interfaces:**
- Consumes: `ITickWriter`, `ICandleWriter`, `ISymbolRepository` (Task 5).
- Produces their Npgsql implementations, registered in `DependencyInjection.AddTickPulseInfrastructure(IServiceCollection, IConfiguration)`. That method uses `NpgsqlDataSource` as a singleton.

**Algorithm** (one transaction per batch; this is the core correctness point):

```sql
-- 1) binary COPY the batch into tick_staging (DELETE FROM tick_staging first, inside the tx)
-- 2) single statement:
WITH ins AS (
  INSERT INTO price_ticks (symbol_id, agg_trade_id, price, quantity, trade_time, ingested_at)
  SELECT symbol_id, agg_trade_id, price, quantity, trade_time, ingested_at FROM tick_staging
  ON CONFLICT DO NOTHING
  RETURNING symbol_id, agg_trade_id, price, quantity, trade_time),
agg AS (
  SELECT symbol_id, time_bucket('1 minute', trade_time) AS bucket_start,
         first(price, agg_trade_id) o, max(price) h, min(price) l, last(price, agg_trade_id) c,
         min(agg_trade_id) fid, max(agg_trade_id) lid, sum(price) ps, count(*) tc,
         sum(quantity) v, sum(price*quantity) qv
  FROM ins GROUP BY 1,2)
INSERT INTO candles_1m AS k (...) SELECT ..., 1 FROM agg
ON CONFLICT (symbol_id, bucket_start) DO UPDATE SET
  open  = CASE WHEN k.source=2 OR excluded.first_trade_id < k.first_trade_id THEN excluded.open  ELSE k.open  END,
  close = CASE WHEN k.source=2 OR excluded.last_trade_id  > k.last_trade_id  THEN excluded.close ELSE k.close END,
  high  = CASE WHEN k.source=2 THEN excluded.high ELSE GREATEST(k.high, excluded.high) END,
  low   = CASE WHEN k.source=2 THEN excluded.low  ELSE LEAST(k.low, excluded.low) END,
  first_trade_id = CASE WHEN k.source=2 THEN excluded.first_trade_id ELSE LEAST(k.first_trade_id, excluded.first_trade_id) END,
  last_trade_id  = CASE WHEN k.source=2 THEN excluded.last_trade_id  ELSE GREATEST(k.last_trade_id, excluded.last_trade_id) END,
  price_sum  = CASE WHEN k.source=2 THEN excluded.price_sum  ELSE k.price_sum  + excluded.price_sum  END,
  tick_count = CASE WHEN k.source=2 THEN excluded.tick_count ELSE k.tick_count + excluded.tick_count END,
  volume     = CASE WHEN k.source=2 THEN excluded.volume     ELSE k.volume     + excluded.volume     END,
  quote_volume = CASE WHEN k.source=2 THEN excluded.quote_volume ELSE k.quote_volume + excluded.quote_volume END,
  source = 1;
```

`CandleWriter.InsertBackfillAsync` inserts with `source=2`, NULL sums/ids, and `ON CONFLICT DO NOTHING`.

- [ ] **Step 1: Write the failing tests:**
  - `WriteBatch_InsertsTicksAndBuildsCandle`: OHLC, sum and count are correct for 4 ticks in one minute
  - `WriteBatch_Twice_LeavesCandlesUnchanged`: replaying the same batch returns 0 inserted, and the candle row is unchanged
  - `WriteBatch_SplitAcrossBatches_MergesOpenCloseByTradeId`: ids 1–2 in batch A and 3–4 in batch B, delivered **B first**, still give open = price(id 1) and close = price(id 4)
  - `StreamTick_ReplacesBackfillCandle`: source becomes 1 and the sums are non-null
  - `Backfill_DoesNotOverwriteStreamCandle`
  - Symbol repository tests: `AddOrReactivate_New_CreatesActive`, `AddOrReactivate_Inactive_Reactivates`, `Deactivate_KeepsRow`
- [ ] **Step 2: Run.** Expected: FAIL. **Step 3: Implement.** **Step 4: Run.** Expected: PASS.
- [ ] **Step 5: Commit** `feat(infra): idempotent tick writer with exact candle upsert`

### Task 8: Market-data queries

**Files:** Create `src/TickPulse.Infrastructure/Persistence/MarketDataQueries.cs`. Test `tests/TickPulse.Integration.Tests/Persistence/MarketDataQueriesTests.cs`

**Interfaces:**
- Consumes: `IMarketDataQueries` (Task 5), `Period.Origin`.

**Rules:**
- `GetAverageAsync(from,to)`:
  - full minutes in `[AlignUp(from), AlignDown(to))` come from `candles_1m WHERE source=1` (Σprice_sum, Σtick_count)
  - the edges `[from, AlignUp(from))` and `[AlignDown(to), to]` come from the raw ticks (sum, count)
  - combine them with `AverageCalculator.Combine`
  - `FirstTickUtc`/`LastTickUtc` are `min`/`max(trade_time)` over the raw ticks in `[from,to]`
  - return null if the count is 0
- `GetClosesAsync`: `SELECT time_bucket(@w, bucket_start, @origin) b, last(close, bucket_start) FROM candles_1m WHERE symbol_id=@id AND bucket_start >= @start AND bucket_start < @end GROUP BY b ORDER BY b`. It covers both sources, and empty buckets are simply absent.
- `GetCandlesAsync`: the last `limit` buckets with OHLC (first/last/max/min, Σvolume), oldest first.
- `ApproximateTickCountAsync` uses `approximate_row_count('price_ticks')`.

- [ ] **Step 1: Write the failing tests:**
  - `Average_EqualsRawAvg_WithDuplicatesReplayed`: 2,000 random ticks across 90 minutes with non-aligned `from`/`to`, every batch written twice, and the result `== ROUND(AVG(price),8)` from raw SQL
  - `Average_IgnoresBackfillCandles`
  - `Average_LessThanWindow_ReportsActualCoverage`
  - `Average_NoTicks_ReturnsNull`
  - `Closes_Week_GroupsMondayBuckets`: candles on Sun 23:59 and Mon 00:00 fall into different weeks
  - `Closes_SkipsEmptyBuckets`
  - `Closes_MixesBackfillAndStream`
  - `Candles_ReturnsOldestFirst_LimitRespected`
- [ ] **Step 2: Run.** Expected: FAIL. **Step 3: Implement.** **Step 4: Run.** Expected: PASS.
- [ ] **Step 5: Commit** `feat(infra): exact 24h average and bucketed SMA queries`, then open the PR `feat: persistence and queries` (Tasks 5–8).

---

## Phase 3 — Binance integration and workers

### Task 9: Binance REST client

**Files:** Create `src/TickPulse.Infrastructure/Binance/BinanceRestClient.cs`. Test `tests/TickPulse.Integration.Tests/Binance/BinanceRestClientTests.cs`, which uses a stub `HttpMessageHandler`.

**Interfaces:**
- Produces: `BinanceRestClient : IExchangeMetadata, IKlineSource, IAggTradeSource`, a typed `HttpClient` with `AddStandardResilienceHandler()`.
- `IsTradingSymbolAsync` calls `GET /api/v3/exchangeInfo?symbol=X`. A 400 with code `-1121` means false; otherwise it returns whether `symbols[0].status == "TRADING"`.
- `GetMinuteKlinesAsync` calls `GET /api/v3/klines?symbol&interval=1m&startTime&endTime&limit`. It parses the array rows with string decimals using the invariant culture.
- `GetAggTradesFromAsync` calls `GET /api/v3/aggTrades?symbol&fromId&limit`.
- Weight guard: read `X-MBX-USED-WEIGHT-1M`. If it is > 4800, delay until the next minute. On 429, honour `Retry-After`. On 418, throw `BinanceBannedException`.

- [ ] **Step 1: Write the failing tests:**
  - `IsTrading_TradingSymbol_True`
  - `IsTrading_UnknownSymbol_False` (400/-1121)
  - `IsTrading_BreakSymbol_False`
  - `Klines_ParsesDecimalStringsInvariantCulture`, run with `CultureInfo.CurrentCulture = bg-BG`
  - `HighUsedWeight_DelaysNextCall`, using a `FakeTimeProvider`
  - `Status429_RespectsRetryAfter`
- [ ] **Step 2: Run.** Expected: FAIL. **Step 3: Implement.** **Step 4: Run.** Expected: PASS. **Step 5: Commit** `feat(infra): binance REST client with weight guard`

### Task 10: WebSocket connection, parser, subscribe throttle, fake server

**Files:**
- Create `src/TickPulse.Infrastructure/Binance/{BinanceWebSocketConnection.cs,StreamMessageParser.cs,SubscribeThrottle.cs}` and `src/TickPulse.Infrastructure/Ingestion/ReconnectBackoff.cs`
- Create `tests/TickPulse.Integration.Tests/Fixtures/FakeBinanceServer.cs`
- Test `tests/TickPulse.Integration.Tests/Binance/WebSocketTests.cs` and `tests/TickPulse.Application.Tests/StreamMessageParserTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record AggTradeEvent(string Symbol, long AggTradeId, decimal Price, decimal Quantity, DateTime TradeTimeUtc, DateTime EventTimeUtc)`
  - `StreamMessageParser.Parse(ReadOnlySpan<byte> utf8)`, which returns a discriminated `StreamMessage`: `AggTrade(AggTradeEvent)` | `CommandAck(long Id)` | `CommandError(long? Id, int Code, string Msg)` | `Unknown`. The combined-stream payload is `{"stream":"btcusdt@aggTrade","data":{"e":"aggTrade","s":"BTCUSDT","a":…,"p":"…","q":"…","T":…,"E":…}}`.
  - `BinanceWebSocketConnection : IAsyncDisposable`, with `Task ConnectAsync(CancellationToken)` (URL `{WsBaseUrl}/stream`), `Task SubscribeAsync(IReadOnlyCollection<string> symbols, CancellationToken)`, `Task UnsubscribeAsync(…)`, `ChannelReader<AggTradeEvent> Trades`, `Task Completion` (faults or completes on close), and `DateTime ConnectedAtUtc`. Stream names are `{symbol.ToLowerInvariant()}@aggTrade`. The command ids increase.
  - `SubscribeThrottle(int perSecond = 4, TimeProvider)` with `ValueTask WaitAsync(CancellationToken)`.
  - `ReconnectBackoff(TimeSpan initial = 1s, TimeSpan max = 30s, Random?)`, which has `TimeSpan Next()` (full jitter, doubling) and `void Reset()`.
  - `FakeBinanceServer : IAsyncDisposable`. It's a Kestrel app on a random port with `Uri WsBaseUrl`, `Uri RestBaseUrl`, `IReadOnlyList<string> ReceivedCommands`, `Task PushTradeAsync(string symbol, long id, decimal price, DateTime t)`, `Task DropAllConnectionsAsync()` and `int ActiveConnections`. It also serves stub `exchangeInfo`, `klines` and `aggTrades` endpoints from an in-memory trade list.

- [ ] **Step 1: Write the failing tests:**
  - parser: `Parse_AggTrade_MapsFields`, `Parse_Ack`, `Parse_Error`, `Parse_Garbage_IsUnknown`
  - `Backoff_DoublesUntilCap_AndResets`
  - `Throttle_LimitsToFourPerSecond` (FakeTimeProvider)
  - against the fake server: `Connect_SubscribeAndReceiveTrade`, `Unsubscribe_StopsTrades`, `ServerDrop_CompletesConnection`
- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Implement** on raw `ClientWebSocket`, which replies to pings automatically. A receive loop assembles multi-frame messages into a pooled buffer. Each send goes through `SubscribeThrottle`.
- [ ] **Step 4: Run.** Expected: PASS. **Step 5: Commit** `feat(infra): binance websocket connection and test server`

### Task 11: Ingestion worker (leader lock, batching, reconcile, rotation, gap-fill)

**Files:**
- Create `src/TickPulse.Infrastructure/Ingestion/{IngestionWorker.cs,TickBatchWriter.cs,SymbolReconciler.cs,GapFiller.cs}`, `src/TickPulse.Infrastructure/Persistence/PostgresLeaderLock.cs` and `src/TickPulse.Application/Ingestion/IngestionState.cs`
- Test `tests/TickPulse.Integration.Tests/Ingestion/IngestionWorkerTests.cs`

**Interfaces:**
- Consumes: Tasks 7, 9 and 10.
- Produces:
  - `IngestionState` (singleton, thread-safe) with:
    - `WsState Status` (`Standby|Connecting|Connected|Disconnected`)
    - `bool IsLeader`
    - `DateTime? ConnectedSinceUtc`, `DateTime? LastReconnectUtc`, `DateTime? NextRotationUtc`
    - `double TicksPerSecond` (10 s EWMA)
    - `double LastLagMs`
    - `IReadOnlyDictionary<string, LivePrice> Latest`
    - `event Action<LivePrice>? PriceUpdated`
  - `public sealed record LivePrice(string Symbol, decimal Price, DateTime TradeTimeUtc)`
  - `PostgresLeaderLock` with `Task<bool> TryAcquireAsync(CancellationToken)` (`pg_try_advisory_lock(0x5449434B)` on a dedicated open connection), `bool IsHeld` (false once the connection breaks) and `ValueTask DisposeAsync()`.

**Behaviour:**
1. Loop: try the lock every 10 s. While not leader, set `Status=Standby`.
2. As leader, connect and subscribe to all active symbols. Pump `Trades` → map the symbol name to an id (cache) → `Tick` → `Channel<Tick>` (bounded, `FullMode.Wait`).
3. `TickBatchWriter` drains the channel by size or delay into `ITickWriter`. After each write it updates the state's lag and TPS and raises `PriceUpdated` with the last price per symbol.
4. `SymbolReconciler` runs on `ISymbolChangeSignal` or every `ReconcileInterval`. It diffs the active DB symbols against the subscribed set and sends one SUBSCRIBE and/or UNSUBSCRIBE per diff.
5. Rotation, make-before-break: at `ConnectedAt + RotationInterval`, open a new connection, subscribe it, start pumping it, then dispose the old one. Overlapping duplicates are absorbed by Task 7.
6. When the connection completes or faults, back off with `ReconnectBackoff` (reset after 60 s connected), reconnect, resubscribe, then run `GapFiller`. For each symbol with a known last id, `GetAggTradesFromAsync(lastId+1, 1000)` is called repeatedly until a returned id ≥ the first id seen on the new connection, or until the page is empty or the 50-page cap is reached. The trades go into the same channel.
7. On shutdown, complete the channel and flush.

- [ ] **Step 1: Write the failing tests** (FakeBinanceServer + TimescaleFixture + the worker hosted through `IHost`):
  - `Ingests_TradesIntoDatabase`
  - `AddDuringReconnect_EventuallySubscribed`: drop the connections, add a symbol while disconnected, and the server sees it subscribed
  - `RemoveThenReadd_SubscribedOnce`: `ReceivedCommands` ends with exactly one active subscription for the symbol
  - `Reconnect_GapFill_RecoversMissedTrades_NoDuplicates`: the trades pushed only to REST during the drop are in the DB and the count is exact
  - `Rotation_OverlapsWithoutLossOrDuplicates`: `RotationInterval = 2s`
  - `SecondInstance_StaysStandby`: two workers on one DB, and only one connects
  - `MalformedMessage_IsSkipped`
- [ ] **Step 2: Run.** Expected: FAIL. **Step 3: Implement.** **Step 4: Run.** Expected: PASS, stable over 5 consecutive runs (`for i in 1..5`).
- [ ] **Step 5: Commit** `feat(ingestion): leader-elected websocket ingestion with gap-fill and rotation`

### Task 12: Backfill worker

**Files:** Create `src/TickPulse.Infrastructure/Backfill/BackfillWorker.cs`. Test `tests/TickPulse.Integration.Tests/Ingestion/BackfillWorkerTests.cs`

**Interfaces:**
- Consumes: `IKlineSource`, `ICandleWriter`, `ISymbolRepository`, `IMarketDataQueries.FirstStreamMinuteAsync` and `IngestionState.IsLeader`.
- Produces: `BackfillWorker : BackgroundService, IBackfillQueue`. It's a no-op when `Backfill:Enabled=false`, and it runs only while leader.
- On start it enqueues the active symbols whose status is not `Completed`. For each symbol it:
  1. sets the status to `Running`
  2. walks from `AlignDown(now − Depth, 1m)` to `FirstStreamMinute ?? now`, in pages of 1000
  3. writes each page with `InsertBackfillAsync`
  4. sets the status to `Completed` with `fromUtc`, or to `Failed` (with a logged reason) on an exception

- [ ] **Step 1: Write the failing tests:**
  - `Disabled_DoesNothing`
  - `Enabled_FillsDepth_InPages`: depth 3 days → 4,320 candles and 5 REST calls
  - `StopsAtFirstStreamMinute`
  - `Failure_SetsFailedStatus`
  - `Rerun_IsIdempotent`
- [ ] **Step 2: Run.** Expected: FAIL. **Step 3: Implement.** **Step 4: Run.** Expected: PASS. **Step 5: Commit** `feat(backfill): kline backfill for long-period SMA`, then open the PR `feat: binance ingestion and backfill` (Tasks 9–12).

---

## Phase 4 — HTTP API

### Task 13: API host, content negotiation, problems, health, docs, rate limiting

**Files:**
- Create `src/TickPulse.Api/Program.cs`, `Problems/{XmlProblemDetailsWriter.cs,ProblemFactory.cs}` and `Telemetry/ClientIpHasher.cs`
- Create `tests/TickPulse.Integration.Tests/Fixtures/ApiFactory.cs`
- Test `tests/TickPulse.Integration.Tests/Api/NegotiationTests.cs` and `HealthTests.cs`

**Interfaces:**
- Produces:
  - `ApiFactory : WebApplicationFactory<Program>`. It uses the TimescaleFixture connection, points Binance at a FakeBinanceServer, sets `Admin:ApiKey=test-key`, and has a `FakeTimeProvider Clock`.
  - `ProblemFactory.Create(HttpContext, AppError | string slug, int status, string detail)`, which returns `ProblemDetails` with `type = https://tickpulse.dev/problems/{slug}` and `traceId`.
  - `XmlProblemDetailsWriter : IProblemDetailsWriter`. `CanWrite` is true when `Accept` prefers `application/xml` or `text/xml` over JSON. It writes `application/problem+xml` using `Microsoft.AspNetCore.Mvc.Formatters.Xml.ProblemDetailsWrapper` with the `DataContractSerializer`. Register it **before** the default writer.
- **Pipeline:**
  1. `AddControllers(o => { o.ReturnHttpNotAcceptable = true; o.RespectBrowserAcceptHeader = true; })` with `.AddXmlDataContractSerializerFormatters()`
  2. `AddProblemDetails`
  3. `UseExceptionHandler()` and `UseStatusCodePages()`, which give the 404/406/401/403 bodies
  4. `ForwardedHeaders`, trusting loopback and Cloudflare via `CF-Connecting-IP` from the trusted proxy
  5. `AddRateLimiter`: a fixed window per client IP on the `public` policy, rejecting with 429 through `IProblemDetailsService` with slug `rate-limited`
  6. OpenAPI (`AddOpenApi`) with Scalar at `/docs`
  7. health checks `/health/live` and `/health/ready`, where ready needs the DB and `IngestionState.Status ∈ {Connected, Standby}`
  8. `UseStaticFiles` and `MapFallbackToFile("index.html")` for paths not starting with `/api`, `/hubs`, `/health` or `/docs`
  9. migrate on startup when `Database:RunMigrationsOnStartup`

- [ ] **Step 1: Write the failing tests.** `Negotiation_Matrix` is a theory over (Accept header → expected content type) for `GET /api/BTCUSDT/24hAvgPrice` with seeded ticks:
  - `null` → `application/json`
  - `*/*` → json
  - `application/xml` → `application/xml`
  - `text/xml` → `text/xml`
  - browser default → xml
  - `application/json;q=0.5, application/xml` → xml
  - `text/csv` → **406** with a problem body

  The same matrix runs against `GET /api/NOPEUSDT/24hAvgPrice`, expecting 404 with `application/problem+json` or `application/problem+xml`. The remaining tests:
  - `UnmatchedRoute_Xml_ReturnsProblemXml`
  - `UnhandledException_ReturnsProblemWithoutStackTrace`, using a test-only throwing endpoint registered in the factory
  - `RateLimit_Exceeded_Returns429Problem`
  - `HealthReady_DbUp_Returns200`
  - `Docs_ServesScalar`
  - `Root_ServesIndexHtml_WhenPresent`
- [ ] **Step 2: Run.** Expected: FAIL. **Step 3: Implement.** **Step 4: Run.** Expected: PASS. **Step 5: Commit** `feat(api): negotiation, xml problem details, health, docs, rate limiting`

### Task 14: Analytics endpoints

**Files:**
- Create `src/TickPulse.Application/Analytics/AnalyticsService.cs`, `src/TickPulse.Contracts/{AveragePriceResponse,SmaResponse,SmaDataPoint,SymbolResponse,CandleResponse}.cs`, `src/TickPulse.Api/Controllers/{AnalyticsController,SymbolsController}.cs`
- Test `tests/TickPulse.Application.Tests/AnalyticsServiceTests.cs` (NSubstitute) and `tests/TickPulse.Integration.Tests/Api/AnalyticsEndpointTests.cs`

**Interfaces:**
- Contracts are `[DataContract(Name=…, Namespace="https://tickpulse.dev/schema/v1")]` classes with `[DataMember(Order=n)]` `init` properties, and the same names are used in JSON (camelCase):
  - `AveragePriceResponse { Symbol, AveragePrice (decimal), From (DateTime), To (DateTime), CoveredSeconds (long), SampleCount (long), WindowHours (int = 24) }`
  - `SmaResponse { Symbol, Period, RequestedDataPoints, ActualDataPoints, Value (decimal), WindowStart, WindowEnd, ReferenceTime, StartParameter (DateTime?), DataPoints (List<SmaDataPoint>) }`
  - `SmaDataPoint { BucketStart, Close }`
  - `SymbolResponse { Symbol, IsActive, LatestPrice (decimal?), Change24hPercent (decimal?), BackfillStatus (string) }`
  - `CandleResponse { BucketStart, Open, High, Low, Close, Volume }`
- `AnalyticsService` returns `AppResult<T>`:
  - `GetAverageAsync(string symbol, CancellationToken)`
  - `GetSmaAsync(string symbol, int n, string p, DateTime? sUtc, CancellationToken)`
  - `ListSymbolsAsync(…)`
  - `GetCandlesAsync(string symbol, string p, int limit, …)`
- Controller routes:
  - `[HttpGet("api/{symbol}/24hAvgPrice")]`
  - `[HttpGet("api/{symbol}/SimpleMovingAverage")]` with `[FromQuery] int n, string p, string? s`
  - `GET api/symbols` (public; `Change24hPercent` from `GetCloseAtOrBeforeAsync(now−24h)` and `IngestionState.Latest`)
  - `GET api/{symbol}/candles?p=&limit=` (limit 1..1000, default 300)
- **Validation → 400 `validation`:** `n` outside 1..1000, unknown `p`, `s` unparseable, or `s` > now. A `s` without an offset is treated as UTC (`DateTimeStyles.AssumeUniversal | AdjustToUniversal`).
- **Errors:** an unknown or never-monitored symbol is 404 `unknown-symbol`, an empty window or no rows is 404 `no-data`, and an inactive symbol with data returns 200.

- [ ] **Step 1: Write the failing tests:**
  - `Avg_ReturnsExactAverageAndCoverage`
  - `Avg_LowercaseSymbol_Works`
  - `Avg_GarbageSymbol_Returns404Problem` (`BTC-USDT`)
  - `Avg_InactiveSymbolWithData_Returns200`
  - `Sma_NoS_UsesLastCompleteBuckets` (fixtures computed by hand)
  - `Sma_WithS_StartsAtS`
  - `Sma_SWithOffset_IsConvertedToUtc` (`s=2026-10-01T03:00:00+03:00` ≡ `00:00Z`)
  - `Sma_FutureS_Returns400`
  - `Sma_InvalidP_Returns400`
  - `Sma_NZero_Returns400`
  - `Sma_FewerPoints_ReportsActual`
  - `Sma_NoData_Returns404`
  - `Sma_Xml_HasSameFields`: parse the XML and assert the element names
  - `Symbols_ListsSeeded`
  - `Candles_ReturnsOldestFirst`
- [ ] **Step 2: Run.** Expected: FAIL. **Step 3: Implement.** **Step 4: Run.** Expected: PASS. **Step 5: Commit** `feat(api): 24h average, SMA, symbols and candles endpoints`

### Task 15: Admin symbols API

**Files:**
- Create `src/TickPulse.Application/Symbols/SymbolAdminService.cs`, `src/TickPulse.Api/Auth/ApiKeyAuthenticationHandler.cs`, `src/TickPulse.Api/Controllers/AdminSymbolsController.cs` and `src/TickPulse.Contracts/AddSymbolRequest.cs`
- Test `tests/TickPulse.Integration.Tests/Api/AdminEndpointTests.cs`

**Interfaces:**
- `SymbolAdminService` has `AddAsync(string raw)`, which returns `AppResult<MonitoredSymbol>` plus a flag saying whether it was created, `RemoveAsync(string raw)` and `ListAsync()`.
- **Add flow:**
  1. `SymbolName.TryCreate`, else 422 `invalid-symbol`
  2. if already active → 200
  3. if the active count is ≥ `MaxActiveSymbols` → 409 `symbol-limit`
  4. if `IExchangeMetadata.IsTradingSymbolAsync` is false → 422 `invalid-symbol`
  5. `AddOrReactivateAsync`, then `ISymbolChangeSignal.Notify()`, then `IBackfillQueue.Enqueue` → 201
- Remove gives 204, or 404 if unknown.
- The scheme is `ApiKey`, using the header `X-Api-Key` and `CryptographicOperations.FixedTimeEquals` over the UTF-8 bytes. When `Admin:ApiKey` is empty, the policy fails with 403 `admin-disabled`.
- Routes: `GET/POST api/admin/symbols` and `DELETE api/admin/symbols/{symbol}`, all `[Authorize(Policy="Admin")]`.

- [ ] **Step 1: Write the failing tests:**
  - `NoKey_Returns401Problem`
  - `WrongKey_Returns401Problem`
  - `KeyNotConfigured_Returns403AdminDisabled`
  - `Add_Valid_Returns201_AndSubscribes` (the fake server receives SUBSCRIBE)
  - `Add_AlreadyActive_Returns200`
  - `Add_InvalidFormat_Returns422`
  - `Add_NotOnExchange_Returns422`
  - `Add_OverLimit_Returns409`
  - `Remove_Returns204_HistoryStillQueryable`
  - `Remove_Unknown_Returns404`
  - `Admin_Problems_HonourXmlAccept`
- [ ] **Step 2: Run.** Expected: FAIL. **Step 3: Implement.** **Step 4: Run.** Expected: PASS. **Step 5: Commit** `feat(api): admin symbol management with api-key auth`

### Task 16: SignalR hub and system stats

**Files:**
- Create `src/TickPulse.Api/Hubs/{MarketHub.cs,MarketBroadcaster.cs}`, `src/TickPulse.Api/Controllers/SystemController.cs` and `src/TickPulse.Contracts/SystemStatsResponse.cs`
- Test `tests/TickPulse.Integration.Tests/Api/HubAndStatsTests.cs`

**Interfaces:**
- `SystemStatsResponse { WsStatus, IsLeader, TicksPerSecond, IngestLagMs, StoredTicksApprox, UptimeSeconds, ConnectedSince, LastReconnect, NextRotation, ActiveSymbols, Version }`
- `GET api/system/stats`
- The hub is at `/hubs/market`. The server calls `price` with `{symbol, price, tradeTime}` and `stats` with `SystemStatsResponse`. Clients call `Subscribe(string symbol)` and `Unsubscribe(string symbol)`, which join the group `sym:{SYMBOL}`.
- `MarketBroadcaster : BackgroundService` subscribes to `IngestionState.PriceUpdated`. It coalesces per symbol and flushes every 250 ms, and pushes stats every 2 s to all clients.

- [ ] **Step 1: Write the failing tests:** `Stats_ReturnsIngestionState` and `Hub_SubscribedClient_ReceivesPrice` (push a trade via the fake server, then a `HubConnection` against `ApiFactory.Server` receives `price` within 3 s).
- [ ] **Step 2: Run.** Expected: FAIL. **Step 3: Implement.** **Step 4: Run.** Expected: PASS. **Step 5: Commit** `feat(api): realtime market hub and system stats`, then open the PR `feat: http api` (Tasks 13–16).

---

## Phase 5 — Console client

### Task 17: `tickpulse` CLI

**Files:** Create `src/TickPulse.Cli/{Program.cs,TickPulseApiClient.cs,Commands/*.cs,Rendering/*.cs}`. Test `tests/TickPulse.Cli.Tests/CliTests.cs`, which uses a stub `HttpMessageHandler` with canned JSON and XML bodies.

**Interfaces:**
- `TickPulseApiClient(HttpClient)` has:
  - `GetAverageAsync(symbol, format)` and `GetSmaAsync(symbol, n, p, s, format)`, which return `ApiResponse<T>(T? Body, string Raw, string ContentType, int Status, ProblemDetails? Problem)`
  - `ListSymbolsAsync()`
  - `AddSymbolAsync(symbol, apiKey)` and `RemoveSymbolAsync(symbol, apiKey)`
- Commands:
  - `avg <symbol>`
  - `sma <symbol> -n|--points <int> -p|--period <1m|5m|30m|1d|1w> [-s|--start <ISO-8601>]`
  - `symbols list` | `symbols add <symbol>` | `symbols remove <symbol>` (`--api-key` or env `TICKPULSE_API_KEY`)
  - `watch <symbol> [--interval 2s]`
- Global options: `--api-url` (env `TICKPULSE_API_URL`, default `https://tickpulse.dev`) and `--format table|json|xml` (default table). json/xml set the `Accept` header and print the raw body with syntax highlighting.
- Exit codes: 0 ok, 1 API 4xx, 2 network/5xx, 64 usage.
- Table rendering uses Spectre panels. The SMA view shows the points table plus a bar chart.

- [ ] **Step 1: Write the failing tests:**
  - `Avg_Table_PrintsAverageAndCoverage` (`TestConsole`)
  - `Avg_FormatXml_SendsAcceptXml_PrintsRaw`
  - `Sma_PassesQueryParamsExactly` (`n=20&p=1d&s=2026-10-01T00:00:00Z`)
  - `ApiProblem_PrintsDetail_Exit1`
  - `NetworkError_Exit2`
  - `SymbolsAdd_SendsApiKeyHeader`
  - `InvalidPeriod_Exit64`
- [ ] **Step 2: Run.** Expected: FAIL. **Step 3: Implement.** **Step 4: Run.** Expected: PASS.
- [ ] **Step 5: Commit** `feat(cli): console client for analytics and admin`, then open the PR `feat: console client`.

---

## Phase 6 — Containers

### Task 18: Dockerfile and local compose

**Files:** Create `Dockerfile`, `docker-compose.yml` and `.env.example`, and update `README.md` with a quick start. Test: the manual verification steps below.

**Interfaces:**
- `Dockerfile` stages:
  - `build` (sdk:10.0, restore/publish Api and Cli)
  - `api` (aspnet:10.0-alpine, non-root `app`, `EXPOSE 8080`, `HEALTHCHECK wget -qO- localhost:8080/health/live`)
  - The `web` stage is added in Task 21, not here.
  - `cli` (runtime:10.0-alpine, `ENTRYPOINT ["dotnet","TickPulse.Cli.dll"]`)
- `docker-compose.yml`:
  - `db` (pinned Timescale image, volume `tickpulse-db`, `pg_isready` healthcheck)
  - `api` (build target `api`, `depends_on: db: condition: service_healthy`, `Backfill__Enabled=true`, `Admin__ApiKey=${ADMIN_API_KEY:-local-dev-key}`, port `8080:8080`)
  - `cli` (profile `cli`, target `cli`, `TICKPULSE_API_URL=http://api:8080`)
  - the observability services under profile `observability` (Task 20)

- [ ] **Step 1: Build.** `docker compose build`. Expected: both images build.
- [ ] **Step 2: Bring it up and check health.** `docker compose up -d`, then `curl -s localhost:8080/health/ready`. Expected: `Healthy` within 60 s.
- [ ] **Step 3: Check the data.** After 2 minutes, `curl -s localhost:8080/api/BTCUSDT/24hAvgPrice` returns JSON with `sampleCount > 0`, and `curl -s -H "Accept: application/xml" …` returns XML.
- [ ] **Step 4: Check the CLI.** `docker compose run --rm cli sma BTCUSDT -n 20 -p 1d` prints 20/20 points once the backfill has completed.
- [ ] **Step 5: Commit** `chore: container images and local compose`, then open the PR.

---

## Phase 7 — Observability

### Task 19: Instrumentation

**Files:**
- Create `src/TickPulse.Infrastructure/Telemetry/TickPulseMetrics.cs` and `src/TickPulse.Api/Telemetry/ApiMetricsMiddleware.cs`
- Modify `Program.cs` and the workers
- Test `tests/TickPulse.Integration.Tests/Telemetry/MetricsTests.cs`, which uses `MetricCollector<T>` from `Microsoft.Extensions.Diagnostics.Testing`

**Interfaces:**
- Meter `TickPulse` with these instruments (OTel names; Prometheus converts the dots to underscores):
  - `tickpulse.ingest.ticks.received{symbol}`, `tickpulse.ingest.ticks.persisted{symbol}`
  - `tickpulse.ingest.lag` (histogram, ms)
  - `tickpulse.ingest.channel.depth` (observable)
  - `tickpulse.ingest.batch.size`, `tickpulse.ingest.batch.duration` (s)
  - `tickpulse.ws.connections{reason=initial|reconnect|rotation}`
  - `tickpulse.ws.state` (observable 0..3)
  - `tickpulse.leader.is_leader` (observable)
  - `tickpulse.backfill.candles{symbol}`, `tickpulse.gapfill.trades{symbol}`
  - `tickpulse.api.responses{route,status,content_type}`
- ActivitySource `TickPulse` spans: `ingest.flush`, `backfill.page`, `gapfill.run`.
- OTel setup: AspNetCore, HttpClient, Runtime, `Npgsql.OpenTelemetry`, and the OTLP exporter when `Otel:Endpoint` is set. Serilog uses `Serilog.Sinks.OpenTelemetry` with the same endpoint, plus a JSON console sink. The enrichers add `service.name=tickpulse-api`, `service.version`, `deployment.environment`, `trace_id` and `span_id`. Request logging includes `client_ip_hash` from `ClientIpHasher`, which is the first 12 hex characters of SHA-256 over the IP plus a salt rotated daily from `Otel:IpSalt`. **Raw IPs are never logged.**

- [ ] **Step 1: Write the failing tests:** `Flush_RecordsPersistedAndLag`, `ApiResponse_RecordsContentTypeTag` (an XML request records `content_type=application/xml`) and `RequestLog_ContainsIpHash_NotRawIp`.
- [ ] **Step 2: Run.** Expected: FAIL. **Step 3: Implement.** **Step 4: Run.** Expected: PASS. **Step 5: Commit** `feat(obs): metrics, traces and structured logs via OTLP`

### Task 20: Observability stack and Grafana as code

**Files:** Create `deploy/observability/**` (configs, provisioning, 4 dashboard JSONs and alert rules) and add the services to `docker-compose.yml` (profile `observability`) and to `deploy/docker-compose.prod.yml`.

**Interfaces:**
- Collector: `otel/opentelemetry-collector-contrib` (pinned) with an OTLP gRPC+HTTP receiver and the `batch` and `memory_limiter` processors. Traces go to Tempo over OTLP, metrics go to Prometheus remote-write (`prometheus --web.enable-remote-write-receiver`), and logs go to Loki's native OTLP (`otlphttp` → `http://loki:3100/otlp`).
- Pinned images: `grafana/grafana`, `grafana/loki`, `grafana/tempo` and `prom/prometheus`. Memory limits: collector 128M, prometheus 256M, loki 256M, tempo 256M, grafana 192M.
- Retention: 14 days for Loki and Tempo, 30 days for Prometheus.
- Grafana datasources (fixed UIDs `prom`, `loki` and `tempo`):
  - Loki has a derived field `trace_id` → Tempo
  - Tempo has `tracesToLogsV2` → Loki
- Dashboards (folder "TickPulse", fixed UIDs `tp-ingestion`, `tp-api`, `tp-runtime` and `tp-logs`) contain the panels listed in spec §8.1, using only the metric names from Task 19. `tp-ingestion` is the home dashboard.
- Alert rules match spec §8.1. The contact point is email via `GF_SMTP_*` env, and the recipient comes from `ALERT_EMAIL`.
- Anonymous access: `GF_AUTH_ANONYMOUS_ENABLED=true`, `GF_AUTH_ANONYMOUS_ORG_ROLE=Viewer`, `GF_USERS_VIEWERS_CAN_EDIT=false`, `GF_SECURITY_ADMIN_PASSWORD` from env, `GF_SERVER_ROOT_URL=https://grafana.tickpulse.dev`.

- [ ] **Step 1: Start the stack.** `docker compose --profile observability up -d`. Expected: all containers healthy.
- [ ] **Step 2: Check the data.** Generate traffic with `for i in $(seq 50); do curl -s -H "Accept: application/xml" localhost:8080/api/ETHUSDT/24hAvgPrice >/dev/null; done`. Open Grafana at `localhost:3000`. Expected: `tp-ingestion` shows ticks/sec per symbol, `tp-api` shows XML vs JSON, and `tp-logs` lists logs whose trace link opens Tempo.
- [ ] **Step 3: Validate the alerts.** `curl -s localhost:3000/api/v1/provisioning/alert-rules | jq length` returns 4.
- [ ] **Step 4: Commit** `feat(obs): grafana stack with dashboards and alerts as code`, then open the PR `feat: observability` (Tasks 19–20).

---

## Phase 8 — Web dashboard

### Task 21: Web scaffold, theme, data layer

**Files:** Create `web/**` (Vite React TS), `web/src/theme.css`, `web/src/api/{client.ts,types.ts,queries.ts}` and `web/src/hooks/useMarketHub.ts`. Modify `.github/workflows/ci.yml` (add the `web` job) and `Dockerfile`: add a `web` stage (node:24-alpine, `npm ci && npm run build`) and `COPY --from=web /web/dist ./wwwroot` in the `api` stage. Test `web/src/api/client.test.ts` and `web/src/hooks/useMarketHub.test.ts` (Vitest).

**Interfaces:**
- `types.ts` mirrors the Contracts DTOs (camelCase).
- `client.ts` has:
  - `getJson<T>(path)`
  - `getRaw(path, accept)`, which returns `{status, contentType, body, durationMs}` for the playground
  - `adminRequest(method, path, apiKey, body?)`
- `queries.ts` wraps TanStack Query: `useSymbols`, `useAverage(symbol)`, `useSma(symbol,n,p,s)`, `useCandles(symbol,p,limit)` and `useStats`.
- `useMarketHub(symbols)` returns `{ status, prices: Record<string, LivePrice>, stats }`, uses automatic reconnect and resubscribes on reconnect.
- `theme.css` has the Tailwind 4 `@theme` tokens for Aurora Glass:
  - `--color-bg #07070d`, `--color-up #5eead4`, `--color-down #fb7185`, `--color-sma #c084fc`
  - glass surface `rgba(255,255,255,.055)` with border `rgba(255,255,255,.11)` and `backdrop-blur`
  - radial glows violet `#3b1d6e` and teal `#0e4a5c`
  - a light theme variant
  - fonts Inter and JetBrains Mono through `@fontsource`, with `font-variant-numeric: tabular-nums` on numbers
- In dev, `vite.config.ts` proxies `/api`, `/hubs` and `/health` to `http://localhost:8080`.

- [ ] **Step 1: Write the failing tests:** `getRaw_ReturnsContentTypeAndBody` (mocked fetch) and `useMarketHub_ResubscribesAfterReconnect` (mocked `HubConnection`).
- [ ] **Step 2: Run** `npm --prefix web test`. Expected: FAIL. **Step 3: Implement**, with ESLint flat config and `tsc --noEmit` in the `typecheck` script. **Step 4: Run** `npm --prefix web run lint && npm --prefix web run typecheck && npm --prefix web test && npm --prefix web run build`. Expected: all pass.
- [ ] **Step 5: Wire CI and commit.** Add the `web` CI job and add `web` to the required checks, then commit `feat(web): scaffold, aurora theme and data layer`.

### Task 22: Sections 1–3 (nav, live tiles, chart and analytics panel)

**Files:** Create `web/src/sections/{Nav.tsx,TickerTiles.tsx,ChartPanel.tsx,AnalyticsPanel.tsx}` and `web/src/components/{Sparkline.tsx,PriceChart.tsx,StatusPill.tsx}`. Test `web/src/sections/AnalyticsPanel.test.tsx`

**Interfaces:**
- `PriceChart` takes `{ candles: CandleResponse[]; sma?: SmaResponse; highlight?: {from,to} }` and uses `lightweight-charts` v5: a candlestick series (up `#5eead4`, down `#fb7185`), a line series for the rolling SMA computed client-side from the candle closes with the same `n`, and a shaded window built from `sma.windowStart..windowEnd` (a primitive or an area series).
- `AnalyticsPanel` holds the `n`, `p` and `s` controls. It shows the value, the "actual / requested points" and the window. The 24h-average card shows the coverage and the sample count. Every number comes from the API.
- `TickerTiles` shows one tile per active symbol with the live price (flashing up/down on change; the flash is disabled under `prefers-reduced-motion`), `change24hPercent` and a sparkline of the last 60 1m closes. The "+ symbol" tile scrolls to the admin section.
- `Nav` contains the brand, the Dashboard, API Docs (`/docs`), Grafana and GitHub links, the theme toggle, and a `StatusPill` showing the WS status and the ingest lag.

- [ ] **Step 1: Write the failing tests:** `AnalyticsPanel_ShowsActualOverRequested` (renders "12 / 20 points" for a mocked response) and `AnalyticsPanel_InvalidN_DisablesQuery`.
- [ ] **Step 2: Run.** Expected: FAIL. **Step 3: Implement**, matching the approved mockup's layout and the 7-section order. **Step 4: Run the tests and build.** Expected: PASS. Then `dotnet run --project src/TickPulse.Api` + `npm --prefix web run dev` and check visually at 1440 px and 390 px widths (no horizontal scroll).
- [ ] **Step 5: Commit** `feat(web): live tiles, price chart with SMA and analytics panel`

### Task 23: Sections 4–7 (playground, admin, system, footer)

**Files:** Create `web/src/sections/{ApiPlayground.tsx,AdminSymbols.tsx,SystemPanel.tsx,Footer.tsx}`. Test `web/src/sections/{ApiPlayground,AdminSymbols}.test.tsx`

**Interfaces:**
- `ApiPlayground`:
  - endpoint picker: 24hAvgPrice, SMA with params, symbols, candles
  - Accept toggle: `application/json` / `application/xml`
  - shows the method and URL, a copyable `curl` command with the `-H "Accept: …"` header, the status, the content type, the duration, and the highlighted raw body
- `AdminSymbols`: the API key input is stored in `sessionStorage` (wrapped in try/catch). It lists the symbols with their backfill status badge, has add (with the errors mapped from problem `detail`) and remove with a confirm step inside the UI (no browser `confirm()`), and polls the backfill status while it's `Pending`/`Running`.
- `SystemPanel` shows the stats from the hub (falling back to `useStats`): ticks/s, lag, stored, uptime, last reconnect and next rotation.
- `Footer` contains the product name, the stack and the links (README, engineering notes, ADRs on GitHub, `/docs`, Grafana).

- [ ] **Step 1: Write the failing tests:** `Playground_XmlToggle_SendsAcceptXml_ShowsRaw`, `Playground_CurlIncludesAcceptHeader`, `Admin_AddError_ShowsProblemDetail` and `Admin_KeyKeptInSessionStorage`.
- [ ] **Step 2: Run.** Expected: FAIL. **Step 3: Implement.** **Step 4: Run** lint, typecheck, tests and build. Expected: PASS. Then run a Lighthouse check of the built app served by the API (`docker compose up`): accessibility ≥ 90 and no console errors.
- [ ] **Step 5: Commit** `feat(web): api playground, admin, system panel and footer`, then open the PR `feat: web dashboard` (Tasks 21–23).

---

## Phase 9 — Deployment

### Task 24: Owner prerequisites (manual, done by the owner with agent guidance)

- [ ] Buy `tickpulse.dev` on Cloudflare Registrar.
- [ ] In Cloudflare, add the DNS records `A tickpulse.dev` and `A grafana.tickpulse.dev`, both Proxied, pointing at the VPS IP. Set SSL to Full (strict), create an Origin certificate covering `tickpulse.dev` and `*.tickpulse.dev`, and place it on the VPS at `/etc/ssl/tickpulse/{origin.pem,origin.key}` (root-only).
- [ ] Create a GitHub Environment `production` with the secrets `DEPLOY_HOST`, `DEPLOY_USER` (=`deploy`), `DEPLOY_SSH_KEY` and `DEPLOY_KNOWN_HOSTS`.
- [ ] On the VPS, as root, create `/srv/tickpulse` owned by `deploy`, plus `/srv/tickpulse/.env` (chmod 600) with:
  - `ADMIN_API_KEY`, `POSTGRES_PASSWORD`, `GF_SECURITY_ADMIN_PASSWORD`
  - `GF_SMTP_*`, `ALERT_EMAIL`
  - `OTEL_IP_SALT`
- [ ] Verify: `ssh deploy@VPS 'docker ps'` works, and `curl -sI https://tickpulse.dev` reaches Cloudflare (a 52x error is fine before the deploy).

Host access details are kept outside the repo.

### Task 25: CD pipeline with smoke test and rollback

**Files:** Create `deploy/docker-compose.prod.yml`, `deploy/smoke.sh`, `deploy/nginx/tickpulse.conf` and `deploy/nginx/grafana.tickpulse.conf`, and modify `.github/workflows/ci.yml` (add the `deploy` job). Update `docs/deployment.md` with the live topology section.

**Interfaces:**
- `docker-compose.prod.yml` uses the project name `tickpulse` and image `ghcr.io/goofy5752/tickpulse-api:${TICKPULSE_TAG}`.
  - `api` is published on `127.0.0.1:8085:8080`, and Grafana on `127.0.0.1:3005:3000`.
  - Every service has `restart: unless-stopped` and `mem_limit` set.
  - The Postgres memory limit is 512M, with `shared_buffers=128MB` set via `command`.
  - Logging uses the `json-file` driver with `max-size: 50m` and `max-file: 3`.
- `smoke.sh <base-url>` exits non-zero unless all of these hold:
  - `/health/ready` returns 200
  - `/api/BTCUSDT/24hAvgPrice` returns 200 JSON with a `sampleCount` field
  - the same request with `Accept: application/xml` returns 200 `application/xml`
  - `/api/BTCUSDT/SimpleMovingAverage?n=5&p=1m` returns 200
- nginx vhosts are `server_name`-scoped and listen on 443 with the Origin cert. They proxy to the loopback ports, set WebSocket upgrade headers for `/hubs/`, and pass `CF-Connecting-IP`.
- The `deploy` job runs when `push` to `main` and needs `[backend, web]`, in environment `production`:
  1. `docker/build-push-action` builds the `api` target and pushes `:${{ github.sha }}` and `:latest` with the gha cache.
  2. `scp` copies `deploy/docker-compose.prod.yml`, `deploy/observability` and `deploy/smoke.sh` to `/srv/tickpulse`.
  3. `ssh` runs: `cd /srv/tickpulse && export TICKPULSE_TAG=<sha> && docker compose -f docker-compose.prod.yml --env-file .env pull && … up -d --remove-orphans`, then polls `/health/ready` for up to 90 s, then runs `./smoke.sh http://127.0.0.1:8085`.
     - On success, write the sha to `.last_good_tag`.
     - On failure, redeploy the `.last_good_tag` tag, re-run the smoke test, and fail the job.

- [ ] **Step 1: Install the nginx vhosts (one time, as root).** Copy the vhost files to `/etc/nginx/sites-available/`, symlink them, run `nginx -t`, then `systemctl reload nginx`. Expected: `nginx -t` is ok, and the other sites still answer (check their status codes before and after).
- [ ] **Step 2: Make the image pullable without auth.** Push to `main` by merging this task's PR, then set the GHCR package `tickpulse-api` to public visibility.
- [ ] **Step 3: Verify the deploy.**
  - The `deploy` job is green.
  - `curl -s https://tickpulse.dev/api/BTCUSDT/24hAvgPrice` returns JSON, and `curl -s -H "Accept: application/xml" …` returns XML.
  - `https://grafana.tickpulse.dev` opens the TickPulse Ingestion dashboard anonymously.
- [ ] **Step 4: Verify the rollback.** On a throwaway branch, temporarily break `/health/ready` and run the workflow via `workflow_dispatch` against `production`. Expected: the smoke test fails, the job rolls back to the previous tag, and the site stays up. Then delete the branch.
- [ ] **Step 5: Set up the uptime check.** Have the owner create a free UptimeRobot monitor on `https://tickpulse.dev/health/ready` with email alerts.
- [ ] **Step 6: Commit** `feat(deploy): GHCR images, SSH deploy, smoke test and rollback`, then open and merge the PR.

---

## Phase 10 — Documentation, AI record, final review

### Task 26: ADRs, engineering notes, README, scale-out design

**Files:** Create `docs/adr/0001-…md` through `docs/adr/0014-…md`, `engineering-notes.md`, `docs/ai/defects-log.md` (maintained since Task 2; finalise it here) and `docs/deployment.md`, and finalise `README.md`.

**Content decisions:**
- **ADRs (Context / Decision / Alternatives / Consequences, ≤ 1 page each):**
  - one for each of the ten §4 decisions
  - single process + leader lock
  - our own candle table rather than a continuous aggregate (with the licensing note)
  - MVC controllers for XML
  - raw `ClientWebSocket`
- **README:**
  - what TickPulse is (2 sentences) and the live links (app, `/docs`, Grafana)
  - a screenshot of the dashboard and an architecture diagram (Mermaid)
  - quick start (`docker compose up`)
  - the configuration table (every key from Global Constraints)
  - database initialisation (automatic migrations + seed; how to disable it)
  - API reference with `curl` examples in JSON and XML
  - CLI usage
  - running the tests
  - the repository layout
- **`engineering-notes.md`:**
  - approach
  - key decisions (linking the ADRs)
  - the AI workflow: how context was provided (spec → plan → `CLAUDE.md` and skills → task prompts), how tasks were dispatched to agents, and how generated code was validated (TDD red/green, real-DB integration tests, the fake exchange, per-task review, a whole-branch review, manual verification against live Binance)
  - the issues and defects caught, with at least the XML ProblemDetails gap, the duplicate-tick double counting, the `DateTimeOffset` XML shape and the Timescale licensing, plus anything found during implementation from `defects-log.md`
  - trade-offs and what's next
  - a note that some of the prompts were written in Bulgarian and are all given in English
- **`docs/deployment.md`:** the live topology plus the **scale-out production design**:
  - Azure Container Apps with API replicas and the ingestion singleton via the advisory lock (or a dedicated worker app with `minReplicas=maxReplicas=1`)
  - Key Vault, App Insights/OTel, blue-green revisions
  - managed Postgres options, verifying the current Azure TimescaleDB support at the time of writing: Timescale Cloud, or PG with native partitioning
  - backup and restore, a SignalR backplane (Azure SignalR) for multi-replica live prices

- [ ] **Step 1: Write the docs.**
- [ ] **Step 2: Check the docs.**
  - Every link resolves (`npx markdown-link-check` on README, notes and docs).
  - Every command in the README works when run from a clean clone.
  - The wording checks from `CLAUDE.local.md` (if present) pass.
- [ ] **Step 3: Commit** `docs: README, engineering notes, ADRs and deployment design`, then open the PR.

### Task 27: Prompt record

**Files:** Create `docs/ai/prompts.md`.

- [ ] **Step 1: Draft it** from the owner's real messages in the Claude Code session transcripts for this project (`~/.claude/projects/D--Programming*/*.jsonl`, the sessions from 2026-11-03 onwards). Group the entries by phase: design, planning, implementation tasks, fixes and reviews. Each entry holds:
  - the prompt, in English
  - its intent
  - the outcome and any correction the owner made

  Include the most significant agent task prompts (from the dispatch messages) as excerpts. Leave out anything unrelated to TickPulse.
- [ ] **Step 2:** Follow `CLAUDE.local.md` (if present), then commit `docs: AI prompt record` and open the PR.

### Task 28: Final whole-repo review and hardening

- [ ] **Step 1: Run a whole-repo review.** Dispatch a fresh reviewer on the whole repository (`superpowers:requesting-code-review`). Focus on correctness of ingestion and SQL, security (admin auth, headers, secrets, logs), the Review Focus items, and the consistency of the docs with the code.
- [ ] **Step 2: Fix the findings** each in its own `fix/` PR. Log every real defect in `docs/ai/defects-log.md`.
- [ ] **Step 3: Verify everything from a clean clone.**
  - `git clone` → `docker compose up` gives a working app.
  - `dotnet test` and `npm --prefix web test` are green.
  - The live smoke test passes: `./deploy/smoke.sh https://tickpulse.dev`.
  - The Grafana dashboards show data for the last 24 h.
- [ ] **Step 4: Report** the final state to the owner: the URLs, the test counts, the coverage numbers, and any open items.
