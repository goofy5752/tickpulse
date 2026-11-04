# 0013. MVC controllers, so JSON and XML are negotiated on every path

- **Status:** Accepted
- **Date:** 2026-11-03

## Context

TickPulse serves JSON and XML through HTTP content negotiation (`Accept`). In ASP.NET Core:

- **minimal APIs** have no output formatters: they write JSON only;
- **MVC controllers** have pluggable output formatters, including `DataContractSerializer` XML, and
  can answer `406` when nothing acceptable can be produced;
- errors produced **outside** controllers (the auth handler's 401, the rate limiter's 429, unmatched
  routes, unhandled exceptions, the 406 itself) go through `IProblemDetailsService`, whose built-in
  writer is JSON-only.

## Decision

- Use MVC controllers with `AddXmlDataContractSerializerFormatters()`, `ReturnHttpNotAcceptable = true`
  and `RespectBrowserAcceptHeader = true`.
- Replace the framework's problem writers with two of our own: `XmlProblemDetailsWriter`
  (`application/problem+xml`) when `Accept` prefers XML, and `JsonProblemDetailsWriter` otherwise.
  Every problem, from controllers, status code pages, the exception handler or the rate limiter, goes
  through them.
- Negotiate by RFC 9110 rules. Higher `q` wins. At equal `q` a concrete type beats a wildcard, and
  between concrete types the first listed wins. `AcceptHeaderNormalization` reorders `Accept` by `q`
  and specificity before MVC sees it, so successes and errors follow the same rule.
- An error is never replaced by a `406`. A client that accepts neither format gets the problem as
  JSON. Only a successful response can be a `406` (itself a `not-acceptable` problem).
- Every `/api` response carries `Vary: Accept`.

## Alternatives

- **Minimal APIs with a hand-written XML result type.** Rejected: it re-implements formatter selection,
  406 handling and model binding errors, and it would still need the custom problem writers.
- **JSON only, with XML through a query string (`?format=xml`).** Rejected: XML must be negotiated
  with `Accept`.

## Consequences

- `NegotiationTests` and the endpoint tests run a matrix of `Accept` values over every endpoint and
  every error path (401, 403, 404, 406, 429, 500), in JSON and XML.
- The end-to-end check found one gap the matrix did not cover: framework-generated 405 and 415 responses
  bypassed the TickPulse problem types (defect F9). PR #16 added the `method-not-allowed` and
  `unsupported-media-type` problems, and `HEAD` on every GET endpoint, with `HttpMethodTests`.
- Controllers add a little per-request overhead compared with minimal APIs; at the measured latencies
  (p95 12.7 ms for `24hAvgPrice` at 6.4k req/s) it does not matter.
