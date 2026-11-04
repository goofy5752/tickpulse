# TickPulse

TickPulse is a .NET 10 market-data service. It collects Binance spot trades over WebSocket, stores them in PostgreSQL with TimescaleDB, and serves the 24-hour average price and simple moving averages over a JSON/XML HTTP API. Admins manage the monitored symbols at runtime, a console client offers the same analytics, and a live dashboard shows prices and system health.

## Quick start

Needs Docker with a recent Docker Compose (tested with Compose 5.1). From the repository root:

```bash
docker compose up
```

This builds the images, starts TimescaleDB and the API, applies the migrations, seeds `BTCUSDT`, `ADAUSDT` and `ETHUSDT`, starts streaming trades from Binance, and backfills about six months of 1-minute candles (a few minutes on first start).

- Dashboard: <http://localhost:8080>
- API reference: <http://localhost:8080/docs>
- Health: <http://localhost:8080/health/ready>

```bash
curl -s localhost:8080/api/BTCUSDT/24hAvgPrice
curl -s -H "Accept: application/xml" "localhost:8080/api/BTCUSDT/SimpleMovingAverage?n=20&p=1d"
curl -s -H "X-Api-Key: local-dev-key" localhost:8080/api/admin/symbols

docker compose run --rm cli avg BTCUSDT          # console client, against the api container
docker compose --profile observability up -d     # plus OTel Collector, Prometheus, Loki, Tempo, Grafana on :3000
docker compose --profile observability down -v   # stop everything and delete the data
```

The admin key `local-dev-key` is a local default. Copy `.env.example` to `.env` to change it, or any other port or setting. The observability stack is described in [`deploy/observability/README.md`](deploy/observability/README.md).

Documentation is in progress.
