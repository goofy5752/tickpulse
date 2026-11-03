---
name: tickpulse-testing
description: Use before writing or changing any code in the TickPulse repo, and before claiming work is done. Sets TDD for Domain, real-database testing with Testcontainers (never the EF in-memory provider), the shared test fixtures, the coverage gates (Domain >= 95% lines, overall >= 80%) and the `Method_Condition_Expected` naming rule.
---

# TickPulse Testing

## Rules

1. **TDD for Domain.** Write the failing test, run it and see it fail for the right reason, then
   implement, then see it pass. No Domain code without a test that drove it.
2. **Real database, always.** Persistence, query and API tests run against TimescaleDB in Docker
   via Testcontainers, with the real migrations applied. Never use the EF Core in-memory provider
   or SQLite as a stand-in; they cannot run `time_bucket`, hypertables or `ON CONFLICT`.
3. **No real network.** Binance is replaced by `FakeBinanceServer` (WS + REST) or a stub
   `HttpMessageHandler`. Tests never call `binance.com`.
4. **No real clock.** Inject `FakeTimeProvider` (`Microsoft.Extensions.TimeProvider.Testing`);
   never `Thread.Sleep`/`Task.Delay` to wait for time to pass. For async conditions, poll with a
   bounded timeout.
5. **Names are `Method_Condition_Expected`**, e.g. `AlignDown_Week_IsMondayMidnightUtc`,
   `WriteBatch_Twice_LeavesCandlesUnchanged`, `Avg_GarbageSymbol_Returns404Problem`.
6. **Assertions use Shouldly**; substitutes use NSubstitute (Application tests only; prefer real
   objects elsewhere).

## Projects

| Project | What | Needs Docker |
|---|---|---|
| `tests/TickPulse.Domain.Tests` | Period alignment, SMA window (both `s` cases), insufficient/gappy data, average merge math | no |
| `tests/TickPulse.Application.Tests` | Use cases against NSubstitute ports, `AppResult` mapping | no |
| `tests/TickPulse.Integration.Tests` | Persistence, queries, ingestion, backfill, API (JSON/XML/406, ProblemDetails, admin auth, validation), Binance client/parser units | yes |
| `tests/TickPulse.Cli.Tests` | CLI commands against an in-memory test server | no |
| `tests/TickPulse.Architecture.Tests` | Layering rules (NetArchTest) | no |

Shared settings live in `tests/Directory.Build.props` (xunit.v3 on VSTest, Shouldly, coverlet)
and `tests/coverlet.runsettings` (excludes generated code and migrations).

## Fixtures (`tests/TickPulse.Integration.Tests/Fixtures/`)

- **`TimescaleFixture`**: xUnit assembly fixture. Starts `timescale/timescaledb:<pinned>-pg17`
  once, applies migrations, exposes `ConnectionString` and `ResetAsync()` (`DELETE FROM` ticks
  and candles, re-seed symbols). Call `ResetAsync()` at the start of each test that writes.
- **`FakeBinanceServer`**: in-test Kestrel app on a random port. `WsBaseUrl`, `RestBaseUrl`,
  `ReceivedCommands`, `PushTradeAsync(...)`, `DropAllConnectionsAsync()`, `ActiveConnections`;
  stub `exchangeInfo`, `klines`, `aggTrades`.
- **`ApiFactory`**: `WebApplicationFactory<Program>` wired to the TimescaleFixture connection and a
  FakeBinanceServer, `Admin:ApiKey=test-key`, and a `FakeTimeProvider Clock`.

## Coverage gates (CI `backend` job)

- `TickPulse.Domain` ≥ **95 %** line coverage.
- Overall ≥ **80 %** line coverage across `TickPulse.*` production assemblies.

CI runs `dotnet test -c Release --collect "XPlat Code Coverage"`, merges the Cobertura files with
ReportGenerator into `coverage/report/Summary.json` and the job summary, and a bash + `jq` step
fails the job below either threshold. It also fails if `TickPulse.Domain` is missing from the
report or has no coverable lines, or if no coverage was collected at all. Reproduce locally with
the commands in `CLAUDE.md`.

Do not lower a threshold or widen an exclusion to get green; add the missing tests.

## Must-have scenarios (keep them green)

- `24hAvgPrice` equals `ROUND(AVG(price), 8)` from raw SQL, with every batch replayed twice.
- Replayed batches leave candles unchanged; split batches delivered out of order merge open/close
  by trade id; backfill never affects the 24 h average.
- Messy symbols (`btcusdt`, ` ETHUSDT `, `BTC-USDT`) normalise or fail cleanly (404/422, never 500).
- `s` on a bucket boundary, in the future (400), without offset (UTC), with `+03:00`.
- `Accept` matrix: browser default → XML, `*/*`/missing → JSON, `application/json;q=0.5,
  application/xml` → XML, `text/csv` → 406 with a problem body; same on error responses.
- Symbol churn during reconnect ends subscribed exactly once.

## Before saying "done"

- [ ] New behaviour has a test that failed first (Domain: always).
- [ ] `dotnet test TickPulse.slnx` green locally with Docker running; you saw the output.
- [ ] Coverage gates still hold.
- [ ] Test names follow `Method_Condition_Expected`.
