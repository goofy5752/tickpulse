# 0011. One deployable process, with ingestion behind a PostgreSQL leader lock

- **Status:** Accepted
- **Date:** 2026-11-03

## Context

TickPulse has two jobs: serve an HTTP API, and keep exactly one WebSocket subscription per symbol
writing into the database. Running two ingesters would double the Binance traffic and race on the
same rows. Running none loses data. The API itself is stateless and can run any number of copies.

## Decision

- **One deployable**: `TickPulse.Api` hosts the API, the SignalR hub and the dashboard, plus two
  hosted workers: `IngestionWorker` and `BackfillWorker`.
- **Leader election** with a session-level PostgreSQL advisory lock (`pg_try_advisory_lock`, key
  `0x5449434B`, "TICK"), held on a dedicated, unpooled connection (`PostgresLeaderLock`).
  - Only the holder ingests and backfills. A standby instance serves the API and retries the lock every
    `Ingestion:LeaderRetryInterval` (10 s).
  - The leader probes its lock connection every `Ingestion:LeaderCheckInterval` (5 s). If the
    connection breaks, the server has already released the lock, so the leader stops ingesting at once.
  - The ingestion worker is the lock's only owner. The backfill worker reads
    `IngestionState.IsLeader` and never touches the lock, so releasing it cannot revoke leadership from
    under another component.
- Symbol changes are signalled in-process, and ingestion also reconciles against the database every
  `Ingestion:ReconcileInterval` (30 s), which picks up changes made through another replica.
- `/health/ready` is ready when the database answers and ingestion is either `Connected` or `Standby`.

## Alternatives

- **A separate worker service plus a message broker.** Rejected: microservices and a broker for a
  two-endpoint service is complexity that does not earn its place. The split is still easy to make
  later (see below).
- **No election, one replica by convention.** Rejected: the first accidental second replica (a
  blue-green overlap, a manual `docker run`) would double-ingest.
- **An external coordinator (Redis, etcd, Kubernetes leases).** Rejected: PostgreSQL is already a hard
  dependency and its advisory locks die with the session, which is exactly the failure semantics
  needed.

## Consequences

- Running more than one instance is safe today: whichever instance holds the lock ingests, and the
  others stand by. That is all the current code guarantees. It does **not** make scale-out a
  configuration change. API-only replicas need a setting that skips the hosted workers, and live
  prices across replicas need a SignalR backplane, because only the leader has them. The other code
  changes are listed in
  [deployment.md](../deployment.md#code-changes-this-design-needs).
- A leadership handover leaves a short gap in the stream. The new leader recovers the missed trades
  over REST ([0022](0022-gap-fill-watermarks-seeded-from-the-database.md)).
- The ingestion workers share the API's memory and CPU. In the end-to-end run on the local stack, ingest
  lag p95 was 803 ms against a 1 s target, most of it the 500 ms batch delay.
