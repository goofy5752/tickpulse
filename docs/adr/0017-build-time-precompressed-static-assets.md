# 0017. Static assets are precompressed at build time; API responses are compressed at the edge

- **Status:** Accepted
- **Date:** 2026-11-04 (PR #16; defect F7 in the [defects log](../ai/defects-log.md#f7))

## Context

The end-to-end check found that nothing was compressed and no static file had `Cache-Control`: the
dashboard's main bundle went out at 411,490 bytes, on every visit.

A first fix added ASP.NET Core response compression for everything (Brotli, then gzip, at `Fastest`)
plus cache headers. It worked, but it cost 10–25 % of the API's loopback throughput on the small JSON
responses, which are most of the traffic. It also broke revalidation at first: the compressed and
plain bodies shared one strong ETag. In production, nginx and Cloudflare sit in front of the API and
compress anyway, so runtime compression in the app bought nothing there.

## Decision

- Precompress the dashboard's static assets **at build time** and serve them with ASP.NET Core
  `MapStaticAssets` (`DashboardAssets`). The Dockerfile copies `web/dist` into `wwwroot` *before*
  `dotnet publish`, so the static web assets pipeline writes a Brotli and a gzip copy of every file, a
  strong ETag per encoding, and the endpoint manifest. Each request gets the variant that
  `Accept-Encoding` allows, with `Vary: Accept-Encoding`.
- Leave API responses **uncompressed** in the app. The reverse proxy (nginx `gzip_proxied any`) and
  Cloudflare compress them on the way out.
- Content-hashed assets under `/assets/` are cached for a year as `immutable`. The SDK cannot see
  Vite's hash as a fingerprint, so a TickPulse rule overrides its `no-cache` for that path. Every other
  file, above all `index.html`, stays `no-cache` and is revalidated by its ETag.

## Alternatives

- **Runtime response compression in the app.** Rejected: measurable CPU cost on every API request for
  no gain behind the proxy, and ETag handling that needs extra middleware to stay correct.
- **No compression in the app at all.** Rejected: the local stack and any deployment without a
  compressing proxy would ship a 400 KB bundle uncompressed.

## Consequences

- Compression of static files costs nothing per request; the work moves to the image build.
- A deployment without a compressing proxy serves API JSON uncompressed. API bodies are small (an SMA
  response with 20 points is about 1.5 KB), so this is acceptable.
- The web build must run before `dotnet publish`, because the manifest is produced at publish time.
  Without a `wwwroot` (as in CI's test runs), the API and health endpoints still work, and the dashboard
  routes return a 404 problem.
- `DashboardAssetsTests` guards it:
  - `PrecompressedAsset_IsServedPerEncoding_Immutable` and
    `PrecompressedAsset_ConditionalGet_Returns304WithTheSameETag`;
  - `PrecompressedIndexHtml_IsRevalidated` and `ApiResponses_AreNeverCompressedByTheHost`;
  - `WithoutWwwroot_ApiAndHealthWork_AndTheDashboardIsA404Problem`.
