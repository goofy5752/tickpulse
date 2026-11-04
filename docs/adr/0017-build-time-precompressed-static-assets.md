# 0017. Static assets are precompressed at build time; API responses are compressed at the edge

- **Status:** Accepted. The implementation is in progress (defect F7 in the
  [defects log](../ai/defects-log.md)).
- **Date:** 2026-11-04

## Context

The live check found that nothing was compressed and no static file had `Cache-Control`: the
dashboard's main bundle went out at 411,490 bytes, on every visit.

A first fix added ASP.NET Core response compression for everything (Brotli, then gzip, at `Fastest`)
plus cache headers. It worked, but it cost 10–25 % of the API's loopback throughput on the small JSON
responses, which are most of the traffic. It also broke revalidation at first: the compressed and
plain bodies shared one strong ETag. In production, nginx and Cloudflare sit in front of the API and
compress anyway, so runtime compression in the app bought nothing there.

## Decision

- Precompress the dashboard's static assets **at build time** and serve them with ASP.NET Core
  `MapStaticAssets`, which serves the precompressed variant that `Accept-Encoding` allows, with an
  ETag of its own, and sets the caching headers.
- Leave API responses **uncompressed** in the app. The reverse proxy (nginx `gzip_proxied any`) and
  Cloudflare compress them on the way out.
- Content-hashed assets under `/assets/` are cached as immutable; `index.html` is always revalidated.

## Alternatives

- **Runtime response compression in the app.** Rejected: measurable CPU cost on every API request for
  no gain behind the proxy, and ETag handling that needs extra middleware to stay correct.
- **No compression in the app at all.** Rejected: the local stack and any deployment without a
  compressing proxy would ship a 400 KB bundle uncompressed.

## Consequences

- Compression of static files costs nothing per request; the work moves to the image build.
- A deployment without a compressing proxy serves API JSON uncompressed. API bodies are small (an SMA
  response with 20 points is about 1.5 KB), so this is acceptable.
- The Dockerfile has to produce the static-asset manifest, which means building the web assets before
  or as part of `dotnet publish` rather than copying `web/dist` in afterwards.
