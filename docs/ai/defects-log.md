# Defects log

The defects caught while TickPulse was built, where they were caught, and what now guards against
them. It is curated: an entry is here when the defect was real (it would have lost data, returned a
wrong number, leaked something, broken an operator workflow or misled a user) and it was fixed or is
being fixed. Style nits and refactors are not listed.

**Where defects were found**

- **Review:** every work item of the implementation plan (`Tn` below is item *n*) got a code review by
  a separate agent, then a re-review of the fixes. See
  [engineering-notes.md](../../engineering-notes.md#how-ai-generated-code-was-validated).
- **E2E run:** an end-to-end run of the full local stack against real Binance: every endpoint in
  JSON and XML, the CLI, admin flows, restarts, the dashboard in Chrome, Grafana, and a load test.
  Its findings are `F1`–`F17`.
- **Load test:** findings that showed up only under the heavier load that followed the E2E run (`F18`,
  and `F19`, which F18's crashes had hidden). They count with the E2E findings: 19 in all, `F1`–`F19`.
- **Security scan:** the automated security review of the CD pipeline.
- **Regression run:** a second live run of the full stack against real Binance, on `main` after the F1–F19
  fixes, which re-checked every earlier finding (the dashboard in headless Chrome included). Its findings are
  `N1`–`N7`.

**Updating an entry.** When a fix merges, put its PR number on the entry and adjust the fix and test lines
if the final version differs.

## Summary

| Id | Defect | Found by | Severity | PR |
|---|---|---|---|---|
| [RV-01](#rv-01) | Tick staging table recreated on every batch | Review (T7) | Important | #3 |
| [RV-02](#rv-02) | A replayed tick older than retention is counted twice in its candle | Review (T7) | Minor | #3 |
| [RV-03](#rv-03) | Binance REST body read had no timeout | Review (T9) | Important | #5 |
| [RV-04](#rv-04) | Singleton workers captured a typed `HttpClient` whose handler never rotated | Review (T9) | Important | #5 |
| [RV-05](#rv-05) | Cancelling a WebSocket command killed the connection | Review (T10) | Important | #5 |
| [RV-06](#rv-06) | The subscribe throttle used the wall clock | Review (T10) | Important | #5 |
| [RV-07](#rv-07) | Keep-alive pongs counted against Binance's 5 msg/s limit | Review (T10) | Important | #5 |
| [RV-08](#rv-08) | A transient database failure dropped a tick batch | Review (T11) | Important | #5 |
| [RV-09](#rv-09) | A resubscribe that was applied but not acked was never gap-filled | Review (T11) | Important | #5 |
| [RV-10](#rv-10) | A rotation committed although the new connection's SUBSCRIBE was rejected | Review (T11) | Important | #5 |
| [RV-11](#rv-11) | A Binance ban failed the whole backfill queue | Review (T12) | Important | #5 |
| [RV-12](#rv-12) | An unexpected worker error would stop the host | Review (T12) | Important | #5 |
| [RV-13](#rv-13) | No CIDR trust for the proxy: every visitor shared one rate-limit bucket | Review (T13) | Important | #7 |
| [RV-14](#rv-14) | `*/*, application/xml` gave JSON for successes and XML for errors | Review (T13) | Important | #7 |
| [RV-15](#rv-15) | Two different shapes of validation problem | Review (T14) | Important | #7 |
| [RV-16](#rv-16) | N+1 queries on `/api/symbols` | Review (T14) | Important | #7 |
| [RV-17](#rv-17) | Admin key comparison leaked the key length through timing | Review (T15) | Important | #7 |
| [RV-18](#rv-18) | A reactivated symbol's inactive stretch was never backfilled | Review (T15) | Important | #7 |
| [RV-19](#rv-19) | A slow stats query stalled live price pushes | Review (T16) | Important | #7 |
| [RV-20](#rv-20) | The CLI echoed the admin API key on stderr | Review (T17) | Important | #9 |
| [RV-21](#rv-21) | The WebSocket alert fired about 6 minutes late | Review (T20) | Important | #6 |
| [RV-22](#rv-22) | Anonymous Grafana viewers could run arbitrary, unbounded queries | Review (T20) | Important | #6 |
| [RV-23](#rv-23) | Re-adding a symbol showed its stale live price | Review (T21) | Important | #4 |
| [RV-24](#rv-24) | A failed period switch left the chart "Loading" forever | Review (T22) | Important | #4 |
| [RV-25](#rv-25) | Third-party GitHub Actions were not pinned | Review (T25), security scan | Important | #11 |
| [RV-26](#rv-26) | `http2` on the vhosts would switch HTTP/2 on for every site on the host's port 443 | Review (T25) | Important | #11 |
| [RV-27](#rv-27) | A rollback restored the image but not the deployed files | Review (T25) | Important | #11 |
| [F1](#f1) | A load burst knocked out telemetry and fired the WebSocket alert falsely | E2E run | Important | #14 |
| [F2](#f2) | Trades lost on every API restart, redeploy or leadership handover | E2E run | Important | #20, #26 |
| [F3](#f3) | `change24hPercent` wildly wrong while a backfill runs | E2E run | Important | #15 |
| [F4](#f4) | Long-period analytics aggregated minutes at query time (p95 83 ms) | E2E run | Important | #12 |
| [F5](#f5) | Uncaught `AbortError`s in the dashboard console | E2E run | Important | #13 |
| [F6](#f6) | The first streamed minute of a symbol kept a partial candle | E2E run | Minor | #20 |
| [F7](#f7) | No compression and no cache headers on static assets | E2E run | Minor | #16 |
| [F8](#f8) | `Retry-After` always the full window; a Warning per rejection | E2E run | Minor | #16 |
| [F9](#f9) | Framework 405/415 bypassed the TickPulse problem types; `HEAD` gave 404 | E2E run | Minor | #16 |
| [F10](#f10) | Expected states logged at Error | E2E run | Minor | #14 |
| [F11](#f11) | 10 s latency spikes from the SignalR hub on the API dashboard | E2E run | Minor | #14 |
| [F12](#f12) | Grafana trace links that anonymous viewers cannot open | E2E run | Minor | #14 |
| [F13](#f13) | "Candles backfilled" under-reported | E2E run | Minor | #15 |
| [F14](#f14) | A reactivated symbol still reported its old `deactivatedAt` | E2E run | Minor | #15 |
| [F15](#f15) | Admin add: no `Location`, and a blank symbol was a 422 | E2E run | Minor | #15 |
| [F16](#f16) | Dead or misleading dashboard links | E2E run | Minor | #13 |
| [F17](#f17) | Symbol lists logged as quoted JSON arrays | E2E run | Minor | #16 |
| [F18](#f18) | The API segfaulted (exit 139) under load | Load test | Critical | #18 |
| [F19](#f19) | The local connection pool exceeded `max_connections`: 20–35 % 500s under load | Load test | Important | #18 |
| [N1](#n1) | Fast symbol switches broke the chart's time scale: `Value is null` on every repaint | Regression run | Important | #25 |
| [N2](#n2) | The minute a symbol is removed in kept a partial candle with a wrong close | Regression run | Minor | #27 |
| [N3](#n3) | "In-flight" counted the dashboards' live hub connections | Regression run | Minor | #27 |
| [N4](#n4) | Every static file became its own `http_route` value | Regression run | Minor | #27 |
| [N5](#n5) | Every 406 logged a framework Warning | Regression run | Minor | #27 |
| [N6](#n6) | Seeded symbols reported `NotRequested` while their backfill was queued | Regression run | Minor | #27 |
| [N7](#n7) | Tempo was killed 1 s after every `down` | Regression run | Minor | #27 |
| [N8](#n8) | A tick flashed a lighter band across the price tiles | Owner, local run | Minor | TBD |

---

## Found by review

### RV-01

**Tick staging table recreated on every batch** · Review (T7) · Important · PR #3

- **Symptom:** every batch ran `CREATE TEMP TABLE` again, adding a catalog write per 500 ms batch.
- **Root cause:** pooled Npgsql connections run `DISCARD ALL` on reuse, which drops session temp tables.
- **Fix:** the tick writer gets its own data source with `NoResetOnClose = true` and `MaxPoolSize = 4`
  ([ADR 0018](../adr/0018-tick-staging-in-a-session-temp-table.md)).
- **Test:** `TickWriterTests.TickWriterDataSource_KeepsEmptyStagingTableBetweenBatches`,
  `WriteBatch_AfterFailedBatch_StartsWithEmptyStaging`,
  `DependencyInjectionTests.AddTickPulseInfrastructure_RegistersPortsAndDataSources`. Each fails when
  `NoResetOnClose` is switched off (mutation check).

### RV-02

**A replayed tick older than retention is counted twice in its candle** · Review (T7) · Minor · PR #3

- **Symptom:** none yet; a latent double count.
- **Root cause:** once retention drops a tick's raw row, a replay of that tick is no longer a primary-key
  conflict. It would be inserted again and added to its candle's `price_sum` and `tick_count` again.
- **Fix:** the writer drops ticks older than `now − 13 days` (`ReplayWindow`, one day inside the 14-day
  retention), with the floor from the injected `TimeProvider`.
- **Test:** `TickWriterTests.WriteBatch_ReplayAfterRetentionDroppedTick_ReturnsZeroAndLeavesCandle`,
  `WriteBatch_TickOlderThanReplayWindow_IsIgnored`, `ReplayWindow_IsInsideRetentionPolicy`.

### RV-03

**Binance REST body read had no timeout** · Review (T9) · Important · PR #5

- **Symptom:** a stalled response body would hang a backfill or gap-fill call for up to 100 s.
- **Root cause:** `HttpClient` buffers the body after the handler pipeline returns, so the resilience
  pipeline's per-attempt timeout never covered it.
- **Fix:** `BinanceAttemptHandler`, the innermost handler, calls `LoadIntoBufferAsync` inside each
  attempt. A stalled body is cut off by the 10 s attempt timeout and retried.
- **Test:** `BinanceRestClientTests.AddBinanceRest_StalledBody_CutOffByAttemptTimeoutAndReportedTransient`
  (fake clock; the mutated run hangs to the test bound).

### RV-04

**Singleton workers captured a typed `HttpClient` whose handler never rotated** · Review (T9) · Important · PR #5

- **Symptom:** the long-lived workers would keep one connection pool forever and never see DNS changes.
- **Root cause:** a typed client resolved into a singleton keeps its first handler.
- **Fix:** `SocketsHttpHandler { PooledConnectionLifetime = 2 min }` as the primary handler, with an
  infinite handler lifetime.
- **Test:** `BinanceRestClientTests.AddBinanceRest_HandlerChain_ResilienceWrapsAttemptHandlerOverPooledSockets`.

### RV-05

**Cancelling a WebSocket command killed the connection** · Review (T10) · Important · PR #5

- **Symptom:** a cancelled `SUBSCRIBE` (for example on a shutdown race or a reconcile timeout) could
  abort a healthy connection with every subscription on it.
- **Root cause:** the caller's token reached `ClientWebSocket.SendAsync`, and a cancelled send aborts the
  socket.
- **Fix:** the send runs on the connection's lifetime token only; the caller's token still covers the
  lock, the throttle and the ack wait.
- **Test:** `WebSocketTests.Subscribe_CanceledWhileThrottled_ConnectionStaysAlive`,
  `Subscribe_CanceledWhileAwaitingAck_ConnectionStaysAlive`.

### RV-06

**The subscribe throttle used the wall clock** · Review (T10) · Important · PR #5

- **Symptom:** a wall-clock jump backwards (NTP correction) would block commands for the length of the
  jump.
- **Fix:** a ring of monotonic timestamps (`TimeProvider.GetTimestamp`).
- **Test:** `SubscribeThrottleTests.Throttle_WallClockJumpsBack_StillReleasesAfterOneSecond` (RED on the
  old code).

### RV-07

**Keep-alive pongs counted against Binance's 5 msg/s limit** · Review (T10) · Important · PR #5

- **Symptom:** unsolicited pongs plus a burst of subscribes could exceed 5 incoming messages per second,
  and Binance disconnects for that.
- **Fix:** `KeepAliveInterval = TimeSpan.Zero`; `ClientWebSocket` still answers Binance's pings.
- **Test:** the fake Binance server now enforces 5 msg/s for every WebSocket test and closes with
  `PolicyViolation` on the sixth (`FakeBinanceServerTests`, `RateLimit_SixMessagesInOneSecond_ClosesConnection`).

### RV-08

**A transient database failure dropped a tick batch** · Review (T11) · Important · PR #5

- **Symptom:** a database restart or failover lost the ticks of the batch in flight, with no gap-fill.
- **Fix:** transient failures (`08xxx`, `57P0x`, `53300`, IO and timeouts) are retried with exponential
  backoff capped at 30 s while back-pressure holds the stream. A batch that is dropped anyway lowers the
  gap-fill watermark to its lowest id, so REST recovers it.
- **Test:** `TickBatchWriterTests.Run_TransientFailures_RetriedUntilStoredWithNothingLost`,
  `Run_TransientFailures_BackOffUpToTheCap`, `Run_PermanentFailure_LowersTheWatermarkPerSymbol`,
  `Run_Abandoned_RecordsUnwrittenTicksAsAGap`.

### RV-09

**A resubscribe that was applied but not acked was never gap-filled** · Review (T11) · Important · PR #5

- **Symptom:** after a reconnect whose `SUBSCRIBE` ack was lost, the trades missed during the outage
  were never fetched.
- **Fix:** symbols are gap-filled after every reconcile in which they were newly acked, not only after the
  first subscribe. Only a fill that reached the stream boundary closes a gap.
- **Test:** `SymbolReconcilerTests.ResubscribeAckLost_OnReconnect_GapFilledOnceAcked` (fails when the
  post-reconcile fill is removed).

### RV-10

**A rotation committed although the new connection's SUBSCRIBE was rejected** · Review (T11) · Important · PR #5

- **Symptom:** the 23 h rotation could switch to a connection that streamed fewer symbols, and close the
  one that streamed them all.
- **Fix:** a rotation commits only when the new connection covers every still-active symbol of the old
  one; otherwise it keeps the old connection and retries after `min(30 s, RotationInterval)`.
- **Test:** `IngestionWorkerTests.Rotation_NewConnectionSubscribeRejected_KeepsTheOldOne` (fails when the
  coverage check is disabled).

### RV-11

**A Binance ban failed the whole backfill queue** · Review (T12) · Important · PR #5

- **Symptom:** one `418` would mark every queued symbol `Failed`.
- **Fix:** a ban marks only the current symbol `Failed`, the worker waits out the ban, then requeues
  failed symbols.
- **Test:** `BackfillWorkerTests.Banned_WaitsOutBan_ThenRequeuesFailedAndContinues`.

### RV-12

**An unexpected worker error would stop the host** · Review (T12) · Important · PR #5

- **Symptom:** any unexpected exception in the backfill or ingestion worker would escape `ExecuteAsync`
  and stop the API with it.
- **Fix:** a top-level catch-all per worker loop: Critical log, reset, bounded backoff, restart
  ([ADR 0020](../adr/0020-hosted-workers-never-crash-the-host.md)).
- **Test:** `BackfillWorkerTests.UnexpectedError_LogsCritical_RestartsAndRecovers`,
  `IngestionWorkerTests.UnexpectedError_LogsCritical_RestartsAndRecovers` (both timed out before the fix).

### RV-13

**No CIDR trust for the proxy: every visitor shared one rate-limit bucket** · Review (T13) · Important · PR #7

- **Symptom:** in a container, requests arrive from the Docker bridge gateway, which was not a trusted
  proxy. `CF-Connecting-IP` was ignored, so every visitor shared the gateway's rate-limit window.
- **Fix:** `ForwardedHeaders:KnownNetworks` (CIDRs) next to `ForwardedHeaders:KnownProxies`; an invalid
  entry fails startup. Production trusts only the network's gateway (`/32`).
- **Test:** `NegotiationTests.RateLimit_TrustsConfiguredNetwork`, `ForwardedHeaders_InvalidKnownNetwork_FailsStartup`.

### RV-14

**`*/*, application/xml` gave JSON for successes and XML for errors** · Review (T13) · Important · PR #7

- **Root cause:** MVC's formatter selection sorts by `q` only and takes the first match, while the
  problem writer preferred the concrete XML type.
- **Fix:** `AcceptHeaderNormalization` reorders `Accept` by `q`, then specificity, before MVC
  ([ADR 0013](../adr/0013-mvc-controllers-for-xml-negotiation.md)).
- **Test:** the `*/*, application/xml` rows in both negotiation matrices, plus unit tests of `Normalize`.

### RV-15

**Two different shapes of validation problem** · Review (T14) · Important · PR #7

- **Symptom:** a bad `n` from the use case gave a problem without `errors`; a binding failure gave one
  with `errors`.
- **Fix:** every validation failure names its parameter and becomes the same `validation` problem with
  `errors[parameter]`, in JSON and XML.
- **Test:** `AnalyticsEndpointTests` validation tests assert the `errors` key and message for `n`, `p`,
  `s` and `limit`, in both formats.

### RV-16

**N+1 queries on `/api/symbols`** · Review (T14) · Important · PR #7

- **Fix:** one batched query for the day-ago closes of all symbols (`CROSS JOIN LATERAL` over the primary
  key) and one for the fallback prices: three queries for any number of symbols.
- **Test:** `AnalyticsServiceTests.ListSymbols_BatchesTheCloseQueries`,
  `MarketDataQueriesTests.ClosesAtOrBeforeQuery_ProbesThePrimaryKeyPerSymbol` (checks the plan).

### RV-17

**Admin key comparison leaked the key length through timing** · Review (T15) · Important · PR #7

- **Root cause:** `FixedTimeEquals` over the raw bytes returns early when the lengths differ.
- **Fix:** both keys are hashed with SHA-256 and the 32-byte digests compared
  ([ADR 0009](../adr/0009-admin-api-key-fails-closed.md)).
- **Test:** timing is not asserted (it cannot be measured reliably in a test).
  `AdminEndpointTests.WrongKey_Returns401Problem` pins the behaviour for shorter, longer, padded and
  differently cased keys.

### RV-18

**A reactivated symbol's inactive stretch was never backfilled** · Review (T15) · Important · PR #7

- **Symptom:** after remove and re-add, the minutes while the symbol was inactive stayed empty.
- **Root cause:** the backfill always ended at the symbol's first-ever stream minute, and reactivation
  cleared `deactivated_at`, so the stretch could not be found.
- **Fix:** `deactivated_at` is kept as "last deactivation"; the backfill walks a second stretch from the
  last stored minute before it to the first stream minute after it.
- **Test:** `BackfillWorkerTests.Reactivated_Pending_FillsTheInactiveStretch_AfterBeingCompleted`,
  `Reactivated_NoNewStreamYet_FillsUpToNow`.

### RV-19

**A slow stats query stalled live price pushes** · Review (T16) · Important · PR #7

- **Symptom:** one slow database count would delay every live price on the hub; a database outage made
  `/api/system/stats` fail with 500; one slow client could hold up every push.
- **Fix:** prices and stats run as separate supervised loops; the counts are cached
  (`SystemStats:CacheTtl`, 2 s) and read with a timeout (`SystemStats:ReadTimeout`, 1 s), falling back to
  the last-known values; every push is bounded to 200 ms.
- **Test:** `HubAndStatsTests.Hub_StalledStatsRead_DoesNotDelayPrices`,
  `Stats_DatabaseDown_Returns200WithLastKnownCounts`.

### RV-20

**The CLI echoed the admin API key on stderr** · Review (T17) · Important · PR #9

- **Symptom:** `tickpulse avg BTCUSDT --api-key s3cret` with a typo elsewhere printed the key in the parse
  error.
- **Fix:** `--api-key` is a recursive root option, and every value that looks like a key (after `-k`,
  any option whose name contains `key`, attached `=`/`:` values, `TICKPULSE_API_KEY`) is replaced with
  `***` in every usage message. A second review round closed the remaining forms (`-kSECRET`,
  `--API-KEY`, `/ApiKey`).
- **Test:** `CliTests.UsageError_NeverEchoesApiKey` (12 cases), `UsageError_EnvironmentKeyInText_IsRedacted`.

### RV-21

**The WebSocket alert fired about 6 minutes late** · Review (T20) · Important · PR #6

- **Fix:** the rule measures the age of the last `Connected` sample, with a cold-start guard and a 30 s
  pending period, so it neither misses outages nor fires on every deploy.
- **Test:** none automated (alert rules are configuration). Checked on a fresh local stack with synthetic
  metrics: Alerting 100 s after the API stopped reporting, and quiet through the first 120 s of a cold
  start.

### RV-22

**Anonymous Grafana viewers could run arbitrary, unbounded queries** · Review (T20) · Important · PR #6

- **Fix:** query limits on Prometheus, Loki and Tempo (samples, length, timeouts, result limits); the
  collector scrubs client addresses, user agents, query strings and headers from logs and traces;
  snapshots and public dashboards are off; production nginx rate-limits `/api/ds/query`.
- **Test:** none automated. Checked on the local stack as an anonymous viewer: write and delete endpoints through the
  datasource proxy answer 403, over-limit Loki and Tempo queries are rejected, and a span carrying six
  sensitive attributes kept only `http.route`. The limits are documented in
  [deploy/observability/README.md](../../deploy/observability/README.md).

### RV-23

**Re-adding a symbol showed its stale live price** · Review (T21) · Important · PR #4

- **Fix:** the dashboard's hub state drops a removed symbol's price instead of hiding it.
- **Test:** `useMarketHub.test.ts` › `useMarketHub_RemovedSymbol_DropsItsPriceSoReAddingDoesNotShowAStaleOne`.

### RV-24

**A failed period switch left the chart "Loading" forever** · Review (T22) · Important · PR #4

- **Fix:** a failed candle load shows an alert with the problem detail and a *Try again* button.
- **Test:** `ChartPanel.test.tsx` (period failure → alert → retry → recovers; first-load failure → empty
  state with retry). The old component failed all 5 tests.

### RV-25

**Third-party GitHub Actions were not pinned** · Review (T25) and security scan · Important · PR #11

- **Fix:** every `uses:` is pinned to a full 40-character commit SHA with a version comment; Dependabot
  keeps them current. The deploy job was split from the image job: it holds the SSH secrets, has
  `contents: read` only, and runs no third-party action.
- **Test:** a check that every `uses:` matches `@[0-9a-f]{40} # vX.Y.Z`.

### RV-26

**`http2` on the vhosts would switch HTTP/2 on for every site on the host's port 443** · Review (T25) · Important · PR #11

- **Root cause:** on nginx 1.18, `http2` is a property of the shared listening socket.
- **Fix:** `listen 443 ssl` without `http2`; visitors still get HTTP/2 and HTTP/3 from Cloudflare. The
  setup checks that the host keeps its own `default_server`.

### RV-27

**A rollback restored the image but not the deployed files** · Review (T25) · Important · PR #11

- **Symptom:** a failed deploy that changed the compose file or the observability config would roll back
  to the old image with the new files.
- **Fix:** `deploy.sh` validates and pulls before touching anything, moves the live files to
  `.previous/`, and on rollback restores them before restarting the last good tag. Nothing a running
  container may read is deleted.
- **Test:** the rollback drill in [deployment.md](../deployment.md#first-time-server-setup) (step 11).

---

## Found by the end-to-end run and load tests

### F1

**A load burst knocked out telemetry and fired the WebSocket alert falsely** · E2E run · Important · PR #14

- **Symptom:** at about 5k req/s the collector sat at its memory limit and refused every signal, Loki
  dropped logs, metrics arrived up to 75 s late, and *WebSocket disconnected* went to Alerting while the
  stream was connected. Telemetry cost 25–55 % of throughput.
- **Root cause:** a trace and an Information line per request; one shared memory limiter; an unbounded
  Loki exporter retry with batches over Loki's 4 MiB limit; an alert that read late metrics as a
  disconnect.
- **Fix:** request sampling at the source ([ADR 0016](../adr/0016-request-sampling-with-429-as-a-sampled-response.md)),
  separate memory limiters with metrics refused last, bounded queues and retries, smaller batches, a
  WebSocket alert based on sample timestamps, and a *Telemetry pipeline down* alert.
- **Test:** `RequestSamplingTests` (a burst of 50,000 at 5k/s keeps about 110), `SamplerTests`,
  `TelemetryHostTests.RequestLog_Burst_LogsOrdinaryRequestsUpToTheBudget_AndEveryProblem`,
  `Tracing_LoggedRequestsAndExportedTraces_AreTheSame`; the collector and alert changes were re-run
  under the same load.

### F2

**Trades lost on every API restart, redeploy or leadership handover** · E2E run · Important · PR #20, PR #26

- **Symptom:** after `docker restart` of the api, `agg_trade_id` gaps (13 BTC and 31 ETH trades in one
  restart) and restart-minute candles that differed from Binance.
- **Root cause:** gap-fill watermarks lived in memory only, so a new process or a new leader had nothing
  to fill from.
- **Fix:** watermarks are seeded from the database at every leadership session
  ([ADR 0022](../adr/0022-gap-fill-watermarks-seeded-from-the-database.md)). The final model is small:
  - Per symbol: `LastId` (the end of the contiguous prefix), a pending flag, a version, and `Above`, the
    highest id any ended connection delivered or any dropped batch reported missing.
  - A start moves up only across ids that were delivered or stored by another instance. `Merge` raises
    a closed entry only over a contiguous span, `Verify` opens a gap when the live stream starts above
    `LastId + 1`, and `Seed` raises only when `Above <= LastId`.
  - A short REST page completes a fill only when it reaches the stream boundary.
- **What the reviews of the fix found** (a first review and five fix rounds): a stale in-memory entry
  beat the seed, and a fill on a broken connection could close a gap; the seed jumped past an open
  gap; trades stored by a discarded rotation link were invisible to the seed; a hole on a busy symbol
  never closed; a completed fill erased the record of a discarded link's trades; a rotation over a
  stalled connection merged without a gap; a lagging REST page closed a gap inside the live stream.
  Round 4 replaced the accumulated flags with the model above. A live re-check on the local stack found
  0 gaps, 0 missing ids and 0 duplicates over restarts.
- **Known limitation:** a hole below the newest stored trade that only another, or a crashed, process
  knew about is invisible to the seed.
- **What the final review found** (fixed in PR #26):
  - *An outage longer than the 1-day seed window left a permanent hole.* The symbol got only a lead-in,
    and the backfill skipped completed symbols. The seed now looks back 12 days: the 13-day replay
    window less a day of headroom for the fill. It reads the newest trade with an ordered ChunkAppend,
    with sorts disabled so the plan cannot fall back to a top-N sort over every chunk. The old
    `max(agg_trade_id)` decompressed and sorted every compressed chunk in the window.
    Every leadership session queues every active symbol in the backfill worker. Klines then fill the
    minutes between the last stored minute and the session's first streamed minute when the outage was
    longer than the seed window. The PR review found a boundary mismatch: the stretch was skipped when its last
    streamed minute *ended* after the seed bound, though its trades could all lie before it, which left up to
    12 days uncovered. The check is now on the minute's start. A completed symbol that never streamed gets
    its stretch too, and re-checks of a symbol the session has not streamed back off from 5 s to 10 minutes.
  - *Rotation race under lagging REST*
    ([issue #19](https://github.com/goofy5752/tickpulse/issues/19)). A new link that had not streamed a
    symbol gave its fill no boundary, so a short REST page closed the gap below ids that only the
    retired link had delivered. A clean rotation now hands the retired link's last ids to the new link,
    and a short page completes the fill only past them.
  - *Gap-fill batches spiked the ingest lag on the stats endpoint and the dashboard.* Only the metric
    skipped them; the state now skips them too.
  - *Replays into compressed chunks were untested.* The 12-day window reaches chunks compressed after 2
    days. A test now compresses a chunk, replays its ticks, and checks that nothing is inserted and
    that candles and rollups are unchanged.
- **Test:** `IngestionWorkerTests.Restart_GapFillsTradesMissedWhileDown_ExactlyOnce`,
  `HandoverAndBack_FillsFromTheOtherLeadersLastStoredTrade`,
  `SeededFill_ConnectionBreaksDuringIt_TradesAfterTheDeadConnectionAreRecovered`,
  `GapLeftOpen_ThenANewSessionInTheSameProcess_RecoversEveryTrade`,
  `DiscardedRotationLink_StoredTradesAboveAGap_NextSessionStillFillsTheGap`,
  `DroppedBatchOnALiveConnection_HoleClosesWithinTheSession`,
  `Rotation_OverAStalledConnection_FillsTheTradesItMissed`,
  `DroppedBatch_RestBehindTheStream_FillDoesNotCloseTheGap_UntilRestCatchesUp`, the
  `GapFillWatermarksTests` rules (`Seed_*`, `NoteStored_*`, `Filled_*`, `Merge_*`, `Verify_*`),
  `GapFillerTests.Fill_ShortPage_*`, `Fill_EmptyPage_*` and `Fill_NoStreamTradeYet_*`,
  `MarketDataQueriesTests.LatestTradeIds*`; for the final-review findings
  `IngestionWorkerTests.RestartAfterAnOutageOfDays_GapFillsFromTheLastStoredTrade`,
  `Rotation_NewLinkSilent_RestBehind_GapStaysOpenUntilTheRetiredLinksLastId`,
  `SeedWindow_LeavesTheFillADayInsideTheReplayWindow`, `BackfillWorkerTests.OutageLongerThanTheSeedWindow_*`,
  `OutageInsideTheSeedWindow_IsLeftToTheTradeGapFill`, `OutageAtTheSeedWindowBoundary_FillsExactlyWhenTheSeedFindsNothing`,
  `CompletedSymbolThatNeverStreamed_FillsUpToThisSessionsStream`, `NotStreamedInThisSession_IsCheckedAgainWithBackoff`,
  `NewLeadershipSession_EvenOneTheWorkerNeverSawStart_ChecksCompletedSymbolsAgain`,
  `TickBatchWriterTests.Run_RecoveredBatch_RecordsNoLagSample`,
  `TickWriterTests.WriteBatch_ReplayIntoACompressedChunk_InsertsNothingAndLeavesCandlesAndRollups`.

### F3

**`change24hPercent` wildly wrong while a backfill runs** · E2E run · Important · PR #15

- **Symptom:** for the first minutes after a start or an add, ETHUSDT showed +42.00 % against Binance's
  −1.5 %.
- **Root cause:** the day-ago reference was "the newest close at or before `now − 24 h`", with no lower
  bound. During an oldest-first backfill that close is the backfill frontier, weeks old.
- **Fix:** until a symbol's backfill completes, the reference close must lie within 15 minutes of
  `now − 24 h`, otherwise the change is `null`. The review of the first version caught that an
  unconditional bound also nulled valid values for thin pairs, so it applies only while the backfill is
  not `Completed`.
- **Test:** `AnalyticsServiceTests.ListSymbols_DayAgoCloseFarBeforeTheWindow_GivesNullChange` (RED
  reproduced 42.00), `ListSymbols_BeforeTheBackfillCompletes_DayAgoCloseMustLieWithinTheTolerance`,
  `ListSymbols_BackfillCompleted_AcceptsAnOlderDayAgoClose`,
  `AnalyticsEndpointTests.Symbols_BackfillBelowTheWindow_HasNullChange`.

### F4

**Long-period analytics aggregated minutes at query time** · E2E run · Important · PR #12

- **Symptom:** SMA `1w n=8` p95 83 ms at 50 connections (target 50 ms); `candles?p=1d` about 135 ms per
  call; database CPU 2,100 %.
- **Fix:** hourly and daily rollups maintained by triggers
  ([ADR 0015](../adr/0015-candle-rollups-maintained-by-triggers.md)). SMA `1w n=8` p95 went from 184.8 to
  20.1 ms on the same data and load.
- **Test:** `CandleRollupTests` (randomised equivalence against the old SQL, overlapping transactions,
  concurrent writers, backfill replacement, deletes, routing, plans, migration backfill).

### F5

**Uncaught `AbortError`s in the dashboard console** · E2E run · Important · PR #13

- **Symptom:** `AbortError: signal is aborted without reason` while typing a two-digit `n` or switching
  periods quickly.
- **Root cause:** each query function passed TanStack Query's `signal` to `fetch`, so superseded queries
  were aborted. Inside the app every rejection was handled, but a browser extension that wraps `fetch`
  leaked them as unhandled rejections (proved with a bare probe and with extensions disabled).
- **Fix:** query functions no longer read `signal`, so the dashboard never aborts a fetch; superseded
  answers land in the cache.
- **Test:** `queries.test.tsx` › `in-flight requests` (7 tests with an extension-style fetch wrapper that
  records leaked rejections; RED 7 of 7 before the fix).

### F6

**The first streamed minute of a symbol kept a partial candle** · E2E run · Minor · PR #20

- **Symptom:** the start minute had the right close but wrong O/H/L/V (BTC volume 5.37 against
  Binance's 13.46).
- **Root cause:** the stream starts mid-minute; the backfill stops before the first stream minute; nothing
  fetched the trades between the minute's start and the first streamed trade.
- **Fix:** a lead-in: the first time a symbol streams in a session, REST finds the first trade of the
  current minute and the gap-fill completes it.
- **Test:** `IngestionWorkerTests.FirstStreamedMinute_IsCompletedFromRest`,
  `ReactivatedSymbol_FirstStreamedMinuteIsCompletedAgain`, `LeadInFailed_IsRetriedOnTheNextConnection`,
  `GapFillerTests.FirstTradeIdFrom_*`.

### F7

**No compression and no cache headers on static assets** · E2E run · Minor · PR #16

- **Symptom:** the 411,490-byte main bundle and `index.html` went out uncompressed, with no
  `Cache-Control`.
- **Fix:**
  - The dashboard goes into `wwwroot` before `dotnet publish`, so the build writes Brotli and gzip
    copies with a strong ETag per encoding, served by `MapStaticAssets`.
  - Hashed `/assets/` files are `immutable` for a year, and `index.html` is `no-cache`.
  - API responses are compressed by nginx and Cloudflare, not by the app
    ([ADR 0017](../adr/0017-build-time-precompressed-static-assets.md)).
  - The first version, with runtime compression, cost 10–25 % of loopback throughput and shared one
    strong ETag between compressed and plain bodies; its own end-to-end check caught the ETag. Its
    review rejected it for the throughput cost.
  - A second round fixed the tests for a missing `wwwroot`.
- **Test:** `DashboardAssetsTests.PrecompressedAsset_IsServedPerEncoding_Immutable`,
  `PrecompressedAsset_ConditionalGet_Returns304WithTheSameETag`, `PrecompressedIndexHtml_IsRevalidated`,
  `ApiResponses_AreNeverCompressedByTheHost`, `MissingAsset_IsNotCachedAsImmutable`,
  `WithoutWwwroot_ApiAndHealthWork_AndTheDashboardIsA404Problem`.

### F8

**`Retry-After` always the full window; a Warning per rejection** · E2E run · Minor · PR #16

- **Symptom:** `Retry-After: 60` on every 429 although the window ended in under 30 s; 685 rejections
  wrote 685 Warnings in 23 s.
- **Root cause:** `FixedWindowRateLimiter`'s failed lease reports the whole window, never the time left.
- **Fix:** a fixed-window limiter of our own whose rejection carries the time left in the
  window, and one Warning per client and window (later rejections at Debug).
- **Test:** `NegotiationTests.RateLimit_RetryAfter_IsTheTimeLeftInTheWindow`,
  `RateLimit_Rejections_LogOneWarningPerClientPerWindow`, `ClientWindowRateLimiterTests`.

### F9

**Framework 405/415 bypassed the TickPulse problem types; `HEAD` gave 404** · E2E run · Minor · PR #16

- **Symptom:** `POST /api/BTCUSDT/24hAvgPrice` gave a 405 with `about:blank`; a `text/plain` body gave a
  415 with an RFC link as its type; `HEAD /api/symbols` gave 404.
- **Fix:** slugs `method-not-allowed` and `unsupported-media-type`, MVC client-error mapping
  off so status code pages write them, and `HEAD` on every GET endpoint.
- **Test:** `HttpMethodTests.WrongMethod_Returns405ProblemWithAllow`, `UnsupportedBody_Returns415Problem`
  (JSON and XML), `Head_OnGetEndpoint_Returns200WithoutBody`, `Head_OnAdminEndpoint_NeedsTheKey`,
  `Head_OnStaticFiles_Returns200WithoutBody`, `OpenApiDocument_DoesNotListHeadOperations` (no HEAD
  operations in OpenAPI).

### F10

**Expected states logged at Error** · E2E run · Minor · PR #14

- **Symptom:** two EF Core Errors on every first boot, and an Error per readiness probe while ingestion
  connected.
- **Fix:** the migrator creates the history table first; ingestion `Connecting`/`Disconnected` is
  `Degraded` (still 503, logged at Warning).
- **Test:** `DatabaseMigratorFreshDatabaseTests.FirstStart_OnAnEmptyDatabase_MigratesWithoutLoggingErrors`
  (RED with exactly the two errors), `HealthTests.HealthReady_IngestionNotStreaming_Returns503Degraded`,
  `TelemetryHostTests.NotReady_WhileIngestionConnects_LogsWarnings_NoErrors`.

### F11

**10 s latency spikes from the SignalR hub on the API dashboard** · E2E run · Minor · PR #14

- **Root cause:** latency queries included `/hubs/market`, whose long-lived WebSocket request lands in the
  top histogram bucket.
- **Fix:** hub and health routes are excluded from the latency and 5xx queries and from the 5xx alert.
- **Test:** none automated (dashboard JSON); the PromQL was checked in Prometheus and the rules load with
  `health: ok`.

### F12

**Grafana trace links that anonymous viewers cannot open** · E2E run · Minor · PR #14

- **Root cause:** Grafana merges a field's `links` override with the datasource's own links.
- **Fix:** the override first unsets the links, then adds *Open trace* to the TickPulse logs dashboard;
  nested span rows are removed.
- **Test:** none automated; checked in Chrome as an anonymous viewer.

### F13

**"Candles backfilled" under-reported** · E2E run · Minor · PR #15

- **Symptom:** the gauge showed 238 K for BTCUSDT while 262,080 klines were stored.
- **Root cause:** `increase()` never sees the first jump of a cumulative series that starts without a zero
  sample.
- **Fix:** per series, end value minus start value, or the whole value for a series that started inside
  the range.
- **Test:** `DashboardQueryTests.CandlesBackfilled_CountsEveryCandleStoredInTheRange` (runs the panel's own
  expression on the real Prometheus image).

### F14

**A reactivated symbol still reported its old `deactivatedAt`** · E2E run · Minor · PR #15

- **Fix:** the admin contract shows `deactivatedAt` only while a symbol is inactive; the stored value,
  which the backfill needs ([RV-18](#rv-18)), is unchanged.
- **Test:** `AdminEndpointTests.Add_Inactive_Reactivates_Returns201`,
  `List_AfterReactivation_ShowsNoDeactivation` (JSON and XML).

### F15

**Admin add: no `Location`, and a blank symbol was a 422** · E2E run · Minor · PR #15

- **Fix:** `GET /api/admin/symbols/{symbol}`; `201` carries `Location` to it; a missing or blank symbol
  is `400 validation` with `errors.symbol`.
- **Test:** `AdminEndpointTests.Add_Created_HasTheLocationOfTheSymbol_WhichServesIt`,
  `Add_MissingSymbol_Returns400Validation` (4 cases), `Add_MissingSymbol_Xml_Returns400Validation`.

### F16

**Dead or misleading dashboard links** · E2E run · Minor · PR #13

- **Symptom:** Grafana links pointed at production on the local stack; the README link used a fragment;
  there was no deployment link; the engineering notes and ADRs did not exist yet.
- **Fix:** the Grafana URL comes from `GRAFANA_ROOT_URL` at image build; repository links are explicit
  `blob/main` and `tree/main` URLs; this documentation adds the missing files.
- **Test:** `links.test.ts` (5), `Footer.test.tsx` (3).

### F17

**Symbol lists logged as quoted JSON arrays** · E2E run · Minor · PR #16

- **Symptom:** `Subscribed 3 symbol(s): "[\"ADAUSDT\",\"BTCUSDT\",\"ETHUSDT\"]"`.
- **Root cause:** Serilog captures an `IReadOnlyList<string>` as a sequence.
- **Fix:** a non-enumerable `SymbolList` value rendered with `{Symbols:l}`.
- **Test:** `TelemetryHostTests.SubscribedLog_ListsSymbolsAsPlainText`.

### F18

**The API segfaulted (exit 139) under load** · Load test · Critical · PR #18

Found while load-testing the telemetry fix ([F1](#f1)). Fixed in the GSS-crash PR, which also carries
[F19](#f19).

- **Symptom:**
  - Under parallel load (3 × `autocannon -c 100`), the API container died with exit code 139, and
    Docker restarted it 16 times in two one-minute rounds.
  - The first crash came about 6 s into the load.
  - Between restarts the API served almost nothing, about 2,000 successful responses a minute.
- **Root cause:**
  - Npgsql's default `GSS Encryption Mode=Prefer` makes every new physical connection call native
    `gss_init_sec_context` (MIT krb5) before TLS and authentication.
  - On the `aspnet:10.0-alpine` (musl) image that call segfaults when many connections open at once.
    All 15 readable crash reports show it (`NpgsqlConnector.GSSEncrypt` → `ld-musl`).
  - Load opens physical connections constantly, at pool growth and after every restart, so one crash
    leads to the next.
  - TickPulse never uses Kerberos. `krb5-libs` had been installed only to silence the probe's
    library-load error, which is what made the probe actually run.
- **Fix:**
  - `NpgsqlConnectionPolicy` forces `GssEncryptionMode=Disable` on every connection the app builds:
    both data sources (and through them EF Core and the migrator), the leader lock, and the design-time
    factory.
  - An explicit mode in the connection string is honoured only with `Database:AllowGssEncryption=true`
    (default `false`).
  - `krb5-libs` leaves the runtime image.
- **Test:** `GssEncryptionStartupTests` (the real host starts, leads and serves with `Prefer` and with
  `Require` in the connection string, and every connection ends up at `Disable`),
  `NpgsqlConnectionPolicyTests.Apply_*`, `DependencyInjectionTests` (`BuildDataSources_ForceGssEncryptionOff_ForDirectCallersToo`,
  `PostgresLeaderLock_WithTheOptIn_KeepsAnExplicitGssEncryptionMode`, `DesignTimeFactory_HasGssEncryptionDisabled`).
  - Under load: 8 + 8 crashes on the old image, 0 in each of 5 rounds on the fixed one.
  - A control run (old image, capped pool) still crashed twice.

### F19

**The local connection pool exceeded `max_connections`: 20–35 % 500s under load** · Load test · Important · PR #18

Found once [F18](#f18) stopped the crashes, which had hidden it. Fixed in the same PR as F18 (#18).

- **Symptom:**
  - Under heavy local load, PostgreSQL rejected connections with `FATAL: sorry, too many clients
    already` (1,069 times in 3 minutes).
  - About 10,000 sockets piled up in `TIME_WAIT` in the API container.
  - Requests then failed with `SocketException 99: Address not available`, as 500s: 20–35 % of all
    responses in three rounds. Throughput was capped at about 2,300 req/s.
- **Root cause:** the local `ConnectionStrings__TickPulse` in `docker-compose.yml` sets no
  `Maximum Pool Size`. The API can therefore hold 100 shared connections plus 4 for the tick writer and
  1 for the leader lock, against PostgreSQL's default `max_connections = 100`. Production sets
  `Maximum Pool Size=20` and is not affected.
- **Fix:** cap the shared pool as production does, by default, through `Database:MaxPoolSize` (20) and an explicit
  `Maximum Pool Size=20` in both compose files, and check the whole connection budget at startup. With a pool of
  30 the same load gave 0 non-2xx at about 8,000–9,500 req/s.
- **Test:** `ComposeConnectionSettingsTests` (both compose files set the default pool size; the connection
  budget fits the production database; every service has a 30-second stop grace period),
  `NpgsqlConnectionPolicyTests.ConnectionBudget_*` and `Apply_NoPoolSizeInTheConnectionString_UsesTheConfiguredSize`,
  `DependencyInjectionTests.Startup_WithMigrationsOff_StillChecksTheConnectionBudget`,
  `HostShutdownTests` (the worst-case stop fits the host timeout and the stop grace period).

## Found by the regression run

### N1

**Fast symbol switches broke the chart's time scale: `Value is null` on every repaint** · Regression run · Important · PR #25

- **Symptom:**
  - On the 1w or 1m chart, switching symbols quickly (ADA, then BTC before ADA's candles arrive)
    threw an uncaught `Error: Value is null` from lightweight-charts' candlestick renderer. It
    repeated on every repaint, about once a second with live trades: 10 errors in a short session.
  - The chart still looked right, so only the console showed it.
- **Root cause:** a bug in lightweight-charts 5.2.1, reached through the order of our `setData` calls.
  - The library has a shortcut for when the series being re-set is the only one holding data: it
    rebuilds its time points as new objects. When the times are unchanged, it keeps its old sorted
    point list, so the list and the point map now hold different objects.
  - The next `setData` on the other series cleans up through the stale list and deletes the live
    points.
  - The time scale then shrank to the SMA's points (8 of 27 weeks), while the candle rows kept their
    old indices, so the renderer looked up rows that were no longer on the scale.
  - The dashboard reached that shortcut during a switch:
    1. While the next symbol loads, the chart redraws the current one without an SMA, so
       `smaSeries.setData([])` left the candles as the only series with data.
    2. The next symbol's candles then arrived on the same week grid, with empty weeks as whitespace.
    3. When the SMA came back, the time scale broke.
  - 5.2.1 is the latest release.
- **Fix:** `setSeriesData` (`web/src/lib/chartSeries.ts`) feeds both series and never leaves the
  SMA series empty while there are candles. Without a line it gets one whitespace point at the first
  candle's time, which draws nothing, so the shortcut is never taken.
- **Test:** `chartSeries.test.ts` drives the real lightweight-charts in jsdom through the exact
  sequence and checks that every candle is still found at its index: `SetSeriesData_FastSymbolSwitch_KeepsEveryCandleOnTheTimeScale`
  failed before the fix (week 25 was lost) and passes after it. Verified in Chrome against the full
  stack: 0 console errors across fast switches on all five periods, against 3–10 before.

### N2

**The minute a symbol is removed in kept a partial candle with a wrong close** · Regression run · Minor · PR #27

The residual part of [F6](#f6): F6 completed the minute a symbol starts streaming in, not the one it stops in.

- **Symptom:** removing a symbol mid-minute left that minute with only the trades streamed before the
  UNSUBSCRIBE (XRP 11:30: close 1.2926 against Binance's 1.2940, volume 1,510 against 26,007), so that
  bucket's 1m SMA close was wrong.
- **Root cause:** nothing fetched the trades between the UNSUBSCRIBE and the end of that minute; a
  reactivation's backfill starts at the minute after the deactivation.
- **Fix:** a tail-out, the mirror of the lead-in. After the UNSUBSCRIBE ack, once the minute the stream
  stopped in is over (plus 5 s), one bounded gap-fill pass fetches `aggTrades?fromId=<last delivered + 1>`
  up to that minute's end. It runs in the background, never delays the unsubscribe, moves no watermark and
  never writes past that minute ([ADR 0022](../adr/0022-gap-fill-watermarks-seeded-from-the-database.md)).
  A reactivation's backfill ends the inactive stretch at the first minute streamed since the reactivation (new
  column `symbols.reactivated_at`), not at the first stream row after the deactivation: when the UNSUBSCRIBE took
  effect late, the old stream and its tail-out store a minute after `deactivated_at`, and the stretch would have
  ended there, unfilled.
  Combined with the rotation handover of [F2](#f2) (PR #26): a symbol removed right after a clean rotation,
  before the new connection delivered it, tails out from the id the retired connection handed over.
- **Test:** `IngestionWorkerTests.RemovedSymbol_LastStreamedMinuteIsCompletedFromRest`,
  `GapFillerTests.TailOut_*`, `BackfillWorkerTests.Reactivated_LateUnsubscribe_StillFillsTheInactiveStretch`,
  `IngestionWorkerTests.RemovedRightAfterACleanRotation_TailsOutFromTheRetiredLinksLastId`.

### N3

**"In-flight" counted the dashboards' live hub connections** · Regression run · Minor · PR #27

- **Symptom:** the API dashboard's In-flight stat read about 10 at an idle 1 req/s.
- **Root cause:** `http_server_active_requests` counts every open request, and each open dashboard holds
  one SignalR request open for as long as it is open. The gauge has no route to filter the hub out.
- **Fix:** the stat subtracts the WebSocket transports of `signalr_server_active_connections` (the
  `Microsoft.AspNetCore.Http.Connections` meter, now exported explicitly; long polling holds no request open between
  polls), clamped at 0; the panel description says so.
- **Test:** `DashboardQueryTests.InFlight_LeavesOutTheLiveHubConnections` (the panel's query on a real
  Prometheus).

### N4

**Every static file became its own `http_route` value** · Regression run · Minor · PR #27

- **Symptom:** the request metrics listed `assets/index-<hash>.js`, every font file and `favicon.svg` as
  routes, plus a route-less series shown as "Value" in the legends.
- **Root cause:** `MapStaticAssets` makes one endpoint per file, with the file path as its route. The names
  carry a content hash, so every deploy added a new set of series.
- **Fix:** the static-asset and fallback endpoints carry `http.route="static"`, and the dashboard responses
  served without an endpoint (the fallback clears its own, `UseStaticFiles` has none) are tagged the same
  way. `tickpulse.api.responses` already counted `/api` only. The per-route legends name the route-less
  series `(no route)`.
- **Test:** `DashboardAssetsTests.ManifestAssets_ShareTheStaticRouteLabel`,
  `DashboardRequests_HaveABoundedRouteLabelSet`.

### N5

**Every 406 logged a framework Warning** · Regression run · Minor · PR #27

- **Symptom:** each `Accept: text/csv` request logged MVC's "No output formatter was found" at Warning, on
  top of the request log line, so one client could write a Warning per request past the request sampling.
- **Fix:** MVC's `ObjectResultExecutor` is built with a logger that writes its Warnings at Debug. The 406
  problem and the request log line (Information) are unchanged.
- **Test:** `NegotiationTests.NotAcceptable_LogsNoFrameworkWarning_AndKeepsTheProblem`.

### N6

**Seeded symbols reported `NotRequested` while their backfill was queued** · Regression run · Minor · PR #27

- **Symptom:** on a first start ADA and ETH showed `NotRequested` ("no history was requested") for 2.5–5
  minutes, until the backfill reached them.
- **Root cause:** the startup scan queued them without changing their status; only the admin use case set
  `Pending`.
- **Fix:** the scan marks every unfinished symbol it queues `Pending`, keeping `backfilled_from`. The update is
  conditional (`is_active` and not `Completed`), so a symbol removed since the scan read it is not left Pending,
  and a completion written meanwhile by another instance is not overwritten.
- **Test:** `BackfillWorkerTests.StartupScan_MarksEveryQueuedSymbolPending`,
  `SymbolRepositoryTests.MarkPendingIfUnfinished_OnlyAnActiveSymbolThatHasNotCompleted`.

### N7

**Tempo was killed 1 s after every `down`** · Regression run · Minor · PR #27

- **Symptom:** `docker compose down` sent SIGTERM to Tempo, then SIGKILL 1 s later (exit 137).
- **Root cause:** no observability service set a `stop_grace_period`, so Compose gave each 1 s. With 30 s,
  Tempo was still killed: on SIGTERM its backend worker waits `finish_on_shutdown_timeout` (default 30 s) for its
  current job, even with none running.
- **Fix:** 30 s on all five, inherited by production through `extends`, and Tempo's
  `backend_worker.finish_on_shutdown_timeout` set to 10 s. A live `down` then stopped every container with
  SIGTERM only, exit 0, Tempo in 10 s.
- **Test:** `ObservabilityComposeTests` (the grace periods, and Tempo's shutdown wait well inside them).

### N8

**A tick flashed a lighter band across the price tiles** · Owner, local run · Minor · PR TBD

- **Symptom:** on a new trade the tile's price flashed, and for a frame a lighter band showed across the top of
  the tile; quick ticks made the flash snap rather than glide.
- **Root cause:** the price element was remounted on every tick (`key`) to restart a keyframe that animated
  `color` and a 22 px `text-shadow`. The large blurred repaint inside a tile with `backdrop-filter` made Chrome
  re-render part of the frosted backdrop, which showed as the band; the remount restarted the animation from its
  start on every tick.
- **Fix:** the price element stays mounted: the digits take the direction's colour at once and ease back by a
  CSS colour transition, and the glow is a separate layer behind the content (`tile-flash`) that animates
  opacity only, restarting from its current opacity so quick ticks swell it again. Reduced motion shows neither.
- **Test:** `TickerTiles.test.tsx`, `Tile_Tick_KeepsThePriceElement_AndEasesTheTintBack`.
