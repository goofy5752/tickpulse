# 0005. `s` anchors the start of the SMA window; without it the window ends now

- **Status:** Accepted
- **Date:** 2026-11-03

## Context

The SMA endpoint takes an optional `s`. TickPulse defines it as the window's **starting date**, and
defines the **current time** as the reference point when `s` is omitted. These two definitions point
in opposite directions: one fixes where the window begins, the other where it ends. Using the same
anchor in both cases would quietly drop one of them.

## Decision

Honour both definitions literally:

- **With `s`:** the window starts at the first bucket whose start is at or after `s` and extends
  forward `n` buckets. It is clipped at the last complete bucket, so a window reaching into the future
  returns fewer points.
- **Without `s`:** the window is the last `n` complete buckets ending at now.

Parsing rules for `s`:

- ISO-8601, with `Z` or a `±hh:mm` offset; without an offset it is UTC.
- A `+` that a client forgot to URL-encode arrives as a space; `2026-10-01T03:00:00 03:00` is read as
  `+03:00` (`Sma_UnencodedPlusOffset_IsAccepted`).
- A future `s` is `400 validation` ("s must not be in the future."). An unparseable `s` is also
  `400 validation`, with `errors.s`.

The response echoes `startParameter` (the parsed `s` in UTC, or `null`), `referenceTime`,
`windowStart` and `windowEnd`, so a client can see which interpretation was applied.

## Alternatives

- **`s` as the end anchor in both cases** (the window is the `n` buckets ending at `s`). Rejected: it
  contradicts the definition of `s` as a starting date.
- **`s` as a start that still ends at now** (ignoring `n`). Rejected: it changes the meaning of `n`.

## Consequences

- `SmaWindow` holds both cases in pure domain code, covered by TDD tests for each, including
  insufficient data and windows that reach past the last complete bucket.
- Clients that expect the "ending at `s`" reading get the window after `s` instead. `windowStart` and
  `windowEnd` in every response make that visible.
