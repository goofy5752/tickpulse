# 0014. A thin wrapper over `ClientWebSocket` for the Binance stream

- **Status:** Accepted
- **Date:** 2026-11-03

## Context

The Binance combined stream has rules that a generic WebSocket client does not know about:

- at most **5 incoming messages per second** per connection, including `SUBSCRIBE`, `UNSUBSCRIBE`
  and pong frames, or Binance disconnects;
- **1024 streams** per connection;
- the server pings every 20 s and closes any connection after **24 hours**;
- `SUBSCRIBE`/`UNSUBSCRIBE` are request-response commands with an `id`, and a command can be accepted,
  rejected or never acknowledged.

The options were a raw `System.Net.WebSockets.ClientWebSocket` wrapper or a library such as
`Websocket.Client`, which adds automatic reconnects and a reactive message stream.

## Decision

Write `BinanceWebSocketConnection`, a small wrapper over `ClientWebSocket`, with one connection per
instance and no automatic reconnect. Reconnects, rotation and gap-fill live one level up, in
`StreamSupervisor`, which knows about symbols and watermarks.

- Commands go through a sliding-window throttle on the monotonic clock (`SubscribeThrottle`, 5 per
  second) and wait for their ack with a timeout.
- `KeepAliveInterval = TimeSpan.Zero`: `ClientWebSocket` still answers Binance's pings, but sends no
  unsolicited pongs that would count toward the 5 msg/s limit.
- Cancelling a command never cancels the socket send: the send runs on the connection's lifetime
  token, so a cancelled `SUBSCRIBE` cannot kill a healthy connection.
- The parser reads `aggTrade` frames from UTF-8 with `Utf8JsonReader`, skips unknown and malformed
  messages with a log line, and parses prices straight into `decimal`.

## Alternatives

- **`Websocket.Client`.** Rejected: its reconnect loop would compete with the supervisor's
  make-before-break rotation and gap-fill, and its throttling and ack handling would still have to be
  written.

## Consequences

- Every Binance rule is explicit and tested against a fake Binance server (Kestrel, in-process), which
  enforces the 5 msg/s limit itself and can drop, delay or refuse acks.
- The 24 h expiry is handled by rotating at `Ingestion:RotationInterval` (23 h): open the new
  connection, subscribe, then close the old one. The primary key absorbs the overlap.
- The review of this code found three real defects (a cancelled command killing the socket, a
  wall-clock throttle, keep-alive pongs against the message limit), all fixed with tests
  (RV-05 to RV-07 in the [defects log](../ai/defects-log.md)).
