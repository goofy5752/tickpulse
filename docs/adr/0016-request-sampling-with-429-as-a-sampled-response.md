# 0016. Request telemetry is sampled at the source; 429 is sampled like a success

- **Status:** Accepted
- **Date:** 2026-11-04 (PR #14)

## Context

Every request produced an exported trace of about 3 spans and an Information log line of about 1 KB.
In the live load test (about 5,000 req/s), the OpenTelemetry Collector sat at its memory limit and
refused every signal, Loki dropped logs, metrics arrived up to 75 s late, and the critical
*WebSocket disconnected* alert fired while the stream was connected. Telemetry also cost 25–55 % of
the API's throughput.

The usual rule "log every 4xx" breaks down for `429`: rejections arrive in floods, exactly when the
pipeline is under the most pressure.

## Decision

- A token bucket, `RequestSampleBudget`, holds `Otel:SampledRequestsPerSecond` requests per second
  (default `10`). Requests within the budget keep their trace and their Information log line; the
  rest are recorded as metrics only.
- `RequestLogPolicy` chooses each request's log level when it completes:

| Request | Level |
|---|---|
| unhandled exception, or 5xx outside `/health` | Error |
| `/health/*` not ready (503) | Warning |
| `/health/*` otherwise | Debug |
| slower than 250 ms | Information |
| 4xx other than 429 | Information |
| 2xx, 3xx and **429** | Information when sampled, Debug otherwise |

- Every request logged at Information or above keeps its trace, so every logged request can be
  opened as a trace.
- `429` is a deliberate exception to "4xx is always logged". The `aspnetcore.rate_limiting.requests`
  metric still counts every rejection. The rate limiter's own rejection Warning is still written for
  every rejection; limiting it to one per client and window is in progress (defect F8 in the
  [defects log](../ai/defects-log.md)).
- `Otel:FlushSampleRatio` (default `0.1`) keeps a share of the `ingest.flush` traces. Backfill pages
  and gap-fills are always traced.

## Alternatives

- **Tail sampling in the collector.** Rejected: the collector itself was the bottleneck, and every
  span would still be exported first.
- **Ratio sampling.** Rejected: at low traffic it drops requests worth seeing, at high traffic it still
  scales with the load.
- **Logging every 429.** Rejected: an abusive client would flood Loki.

## Consequences

- `RequestSamplingTests` drives a burst of 50,000 requests at 5,000/s and checks that about 110 are
  recorded, and that problems never use up the budget. `TelemetryHostTests` checks through the real
  host that the logged requests and the exported traces are the same set.
- Individual successful requests beyond 10/s, and most individual 429s, are not in Loki or Tempo.
  The metrics count all of them.
