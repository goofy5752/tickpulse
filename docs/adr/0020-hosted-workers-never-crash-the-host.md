# 0020. Hosted workers never crash the host

- **Status:** Accepted
- **Date:** 2026-11-04

## Context

The API process hosts three long-running workers: ingestion, backfill and the SignalR broadcaster.
In .NET, an exception that escapes a `BackgroundService` stops the whole host by default
(`BackgroundServiceExceptionBehavior.StopHost`). One unexpected bug in, say, the backfill would then
take down the public API with it, and a restart loop would follow.

The review of the backfill worker found exactly this: an unexpected error type would have escaped
`ExecuteAsync` and stopped the host.

## Decision

- Each worker catches expected failures where they happen (database outages, Binance errors, bans) and
  handles them specifically.
- Around that, each worker's loop has a **top-level catch-all**: log at Critical with the exception,
  reset its state (for ingestion: stand by and release the lock), wait a bounded backoff, and start
  over.
  - Ingestion: `RestartDelay`, doubling from `Ingestion:LeaderRetryInterval`, capped at 30 s.
  - Backfill: 30 s, after which the interrupted symbol is picked up again by the next scan.
  - Broadcaster: its price loop and stats loop are supervised separately and restart after 5 s.
- Shutdown cancellation still ends a worker normally.
- `Program.cs` also sets `BackgroundServiceExceptionBehavior.Ignore`, as a last line of defence.

## Alternatives

- **Fail fast and let the container restart.** Rejected: one worker's bug would take the API down, and
  every restart costs a gap-fill and a reconnect.
- **Catch nothing broad, only known exceptions.** Rejected as the only layer: the unknown case is the
  one that matters here.

## Consequences

- A persistent bug shows up as a loop of Critical log lines (visible in Grafana and its alerting)
  instead of a dead service. That trade-off is intentional.
- `BackfillWorkerTests.UnexpectedError_LogsCritical_RestartsAndRecovers` and
  `IngestionWorkerTests.UnexpectedError_LogsCritical_RestartsAndRecovers` inject a one-off unexpected
  exception and assert one Critical log, a worker that keeps running, and full recovery afterwards.
