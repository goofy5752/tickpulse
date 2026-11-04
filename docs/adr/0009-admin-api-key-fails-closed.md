# 0009. Admin endpoints use one API key and fail closed

- **Status:** Accepted
- **Date:** 2026-11-03

## Context

Adding and removing symbols changes what the service subscribes to and how many REST calls it makes
to Binance, so it needs protection. TickPulse has no user accounts (a non-goal), and one operator
manages it.

## Decision

- Admin routes (`/api/admin/symbols…`) require the header `X-Api-Key` with the key configured in
  `Admin:ApiKey` (environment `Admin__ApiKey`).
- The comparison is constant-time in both content and length: the configured key is hashed with
  SHA-256 once at startup, each presented key is hashed into a stack buffer, and the two digests are
  compared with `CryptographicOperations.FixedTimeEquals` (`AdminApiKey`).
- **No key configured → the admin endpoints are disabled**: `403` with the `admin-disabled` problem.
  A missing or wrong key is `401 unauthorized`.
- A key with leading or trailing whitespace fails startup, since no header could ever match it. A key
  shorter than 32 characters fails startup in the Production environment; in Development and in the
  tests it works and logs a warning at startup. The key itself is never logged.
- Authentication runs after the rate limiter, so key guessing is throttled like any other traffic.
- At most `Admin:MaxActiveSymbols` (default 20) symbols are active at once; one more is
  `409 symbol-limit` ([0019](0019-atomic-symbol-add-under-an-advisory-lock.md)).

## Alternatives

- **No authentication.** Rejected: anyone could subscribe the service to hundreds of symbols and burn
  its Binance weight.
- **Full identity (OIDC, JWT, users and roles).** Rejected: a large surface for one operator, and user
  accounts are a non-goal.

## Consequences

- A fresh install without `ADMIN_API_KEY` exposes no admin surface at all. The local compose file sets
  `local-dev-admin-key-not-for-production` on purpose; it has 38 characters, because the image runs as
  Production. The production compose file requires the variable (`${ADMIN_API_KEY:?}`).
- Rotating the key is an environment change and a container restart.
- The CLI redacts the key from every usage error, so a mistyped command line does not echo it
  (defect RV-20 in the [defects log](../ai/defects-log.md)). The key-length timing leak the review
  found in the first comparison is RV-17.
