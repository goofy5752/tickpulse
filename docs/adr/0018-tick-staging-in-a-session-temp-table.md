# 0018. Ticks are staged in a per-session temp table

- **Status:** Accepted
- **Date:** 2026-11-03

## Context

The tick writer turns a batch of up to `Ingestion:BatchMaxSize` (5,000) ticks into rows in
`price_ticks` and merged minutes in `candles_1m`, in one transaction. The fastest way to move a batch
into PostgreSQL is binary `COPY`, but `COPY` cannot do `ON CONFLICT`. So the batch is copied into a
staging table first, then inserted with `ON CONFLICT DO NOTHING`, and only the rows that were really
inserted (`RETURNING`) feed the candle upsert.

Two writers can run at the same time: the stream's batch writer and the gap-fill. A shared, permanent
staging table would let one see the other's staged rows.

## Decision

- Stage in `tick_staging`, a **session temp table**:
  `CREATE TEMP TABLE IF NOT EXISTS tick_staging (LIKE price_ticks INCLUDING DEFAULTS) ON COMMIT DELETE ROWS`.
  Each connection has its own, and it is empty at the end of every transaction, committed or rolled
  back.
- The tick writer uses its own `NpgsqlDataSource` with `NoResetOnClose = true` and
  `MaxPoolSize = 4`. Without that, the pool's `DISCARD ALL` on reuse would drop the temp table, and
  every batch would create it again.
- Ticks older than `now − 13 days` are dropped by the writer, one day inside the 14-day retention.
  A replayed tick whose raw row retention has already removed would otherwise be inserted again and
  counted into its candle a second time.

## Alternatives

- **A permanent `tick_staging` table.** Rejected: concurrent writers would see each other's rows,
  or need a batch id column and a cleanup.
- **Multi-row `INSERT … ON CONFLICT` with parameters.** Rejected: 5,000 rows × 6 columns is 30,000
  parameters per statement, close to PostgreSQL's limit of 65,535, and slower to bind than a binary
  `COPY` stream.
- **The shared data source.** Rejected: `DISCARD ALL` made the temp table a per-batch cost.

## Consequences

- The cached temp table keeps the shape `price_ticks` had when it was created. That is safe because
  migrations always run before the writer starts.
- `TickWriterTests` pins the behaviour: the staging table survives between batches on the same pooled
  connection and is empty, a failed batch leaves nothing behind, and two concurrent writers never see
  each other's rows.
