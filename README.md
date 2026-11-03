# TickPulse

TickPulse is a .NET 10 market-data service. It collects Binance spot trades over WebSocket, stores them in PostgreSQL with TimescaleDB, and serves the 24-hour average price and simple moving averages over a JSON/XML HTTP API. Admins manage the monitored symbols at runtime, a console client offers the same analytics, and a live dashboard shows prices and system health.

Documentation is in progress.
