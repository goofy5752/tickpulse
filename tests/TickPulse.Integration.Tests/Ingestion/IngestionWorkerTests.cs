using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using TickPulse.Application.Abstractions;
using TickPulse.Application.Ingestion;
using TickPulse.Domain;
using TickPulse.Infrastructure;
using TickPulse.Infrastructure.Ingestion;
using TickPulse.Infrastructure.Persistence;
using TickPulse.Infrastructure.Telemetry;
using TickPulse.Integration.Tests.Fixtures;
using static TickPulse.Integration.Tests.Ingestion.IngestionTestHost;

namespace TickPulse.Integration.Tests.Ingestion;

public sealed class IngestionWorkerTests(TimescaleFixture db) : IAsyncLifetime
{
    private static readonly string[] Seeded = ["BTCUSDT", "ADAUSDT", "ETHUSDT"];

    private readonly FakeBinanceServer _server = new();

    public async ValueTask InitializeAsync()
    {
        await db.ResetAsync();
        await _server.InitializeAsync();

        // Like Binance: more than 5 incoming messages in a second closes the connection.
        _server.MaxIncomingMessagesPerSecond = 5;
    }

    public async ValueTask DisposeAsync()
    {
        var rateLimitDisconnects = _server.RateLimitDisconnects;
        await _server.DisposeAsync();
        rateLimitDisconnects.ShouldBe(0);
    }

    [Fact]
    public async Task Ingests_TradesIntoDatabase()
    {
        await using var host = await StartAsync(db, _server);
        var updates = new ConcurrentQueue<LivePrice>();
        host.State.PriceUpdated += updates.Enqueue;
        await WaitForSeededSubscriptionAsync(host);

        var t0 = Now.AddSeconds(-5);
        for (var i = 1; i <= 20; i++)
        {
            await _server.PushTradeAsync("BTCUSDT", i, 100m + i, t0.AddMilliseconds(i));
        }

        for (var i = 1; i <= 5; i++)
        {
            await _server.PushTradeAsync("ETHUSDT", 1000 + i, 3000m + i, t0.AddMilliseconds(i));
        }

        await WaitForTradeCountAsync(db, "BTCUSDT", 20);
        await WaitForTradeCountAsync(db, "ETHUSDT", 5);
        await WaitForAsync(
            () => host.State.Latest.TryGetValue("BTCUSDT", out var p) && p.Price == 120m
                && host.State.Latest.TryGetValue("ETHUSDT", out var e) && e.Price == 3005m,
            "latest prices");

        (await TradeIdsAsync(db, "BTCUSDT")).ShouldBe(Enumerable.Range(1, 20).Select(i => (long)i));
        (await TradeIdsAsync(db, "ETHUSDT")).ShouldBe(Enumerable.Range(1001, 5).Select(i => (long)i));
        (await CandleTickCountAsync(db, "BTCUSDT")).ShouldBe(20);

        var state = host.State;
        state.IsLeader.ShouldBeTrue();
        state.Status.ShouldBe(WsState.Connected);
        state.ConnectedSinceUtc.ShouldNotBeNull().Kind.ShouldBe(DateTimeKind.Utc);
        state.NextRotationUtc.ShouldBe(state.ConnectedSinceUtc + TimeSpan.FromHours(23));
        state.LastReconnectUtc.ShouldBeNull();
        state.Latest["BTCUSDT"].ShouldBe(new LivePrice("BTCUSDT", 120m, t0.AddMilliseconds(20)));
        state.LastLagMs.ShouldBeGreaterThan(0);
        state.TicksPerSecond.ShouldBeGreaterThan(0);
        updates.ShouldContain(new LivePrice("ETHUSDT", 3005m, t0.AddMilliseconds(5)));

        // One SUBSCRIBE carried every active symbol; nothing else was sent.
        var command = Parse(_server.ReceivedCommands.ShouldHaveSingleItem());
        command.Method.ShouldBe("SUBSCRIBE");
        command.Params.ShouldBe(Seeded.Select(Stream), ignoreOrder: true);
    }

    [Fact]
    public async Task AddDuringReconnect_EventuallySubscribed()
    {
        await using var host = await StartAsync(db, _server);
        await WaitForSeededSubscriptionAsync(host);

        _server.RefuseConnections = true;
        await _server.DropAllConnectionsAsync();
        await WaitForAsync(() => host.State.Status is WsState.Disconnected or WsState.Connecting, "the worker noticed the drop");

        // While no connection can be made: add a symbol the way the admin use case does.
        (await host.Symbols.AddOrReactivateAsync(Symbol("XRPUSDT"), 20, TestContext.Current.CancellationToken))
            .Status.ShouldBe(SymbolAddStatus.Created);
        host.Signal.Notify();
        await WaitForAsync(() => host.Logs.Count("LogConnectFailed") >= 1, "a refused reconnect attempt");
        _server.RefuseConnections = false;

        await _server.WaitUntilAsync(
            s => s.ActiveConnections == 1 && s.SubscribedStreams.SetEquals([.. Seeded.Select(Stream), Stream("XRPUSDT")]),
            WaitTimeout,
            TestContext.Current.CancellationToken);
        await WaitForAsync(() => host.State.Status == WsState.Connected, "connected again");
        await _server.PushTradeAsync("XRPUSDT", 7, 0.5m, Now.AddSeconds(-1));
        await WaitForTradeCountAsync(db, "XRPUSDT", 1);

        host.State.LastReconnectUtc.ShouldNotBeNull();
        _server.AcceptedConnections.ShouldBe(2);
    }

    [Fact]
    public async Task RemoveThenReadd_SubscribedOnce()
    {
        await using var host = await StartAsync(db, _server);
        await WaitForSeededSubscriptionAsync(host);
        var eth = Symbol("ETHUSDT");
        var ct = TestContext.Current.CancellationToken;

        (await host.Symbols.DeactivateAsync(eth, ct)).ShouldBeTrue();
        host.Signal.Notify();
        await _server.WaitUntilAsync(s => !s.SubscribedStreams.Contains(Stream("ETHUSDT")), WaitTimeout, ct);

        (await host.Symbols.AddOrReactivateAsync(eth, 20, ct)).Status.ShouldBe(SymbolAddStatus.Reactivated);
        host.Signal.Notify();
        await _server.WaitUntilAsync(s => s.SubscribedStreams.Contains(Stream("ETHUSDT")), WaitTimeout, ct);

        // Churn faster than the reconciler runs; the coalesced signal must still converge.
        for (var i = 0; i < 5; i++)
        {
            await host.Symbols.DeactivateAsync(eth, ct);
            host.Signal.Notify();
            await host.Symbols.AddOrReactivateAsync(eth, 20, ct);
            host.Signal.Notify();
        }

        // A marker symbol: once it is subscribed, a pass that read the final state of ETHUSDT has completed.
        await host.Symbols.AddOrReactivateAsync(Symbol("XRPUSDT"), 20, ct);
        host.Signal.Notify();
        await _server.WaitUntilAsync(s => s.SubscribedStreams.Contains(Stream("XRPUSDT")), WaitTimeout, ct);

        var net = _server.ReceivedCommands.Select(Parse)
            .Sum(c => !c.Params.Contains(Stream("ETHUSDT")) ? 0 : c.Method == "SUBSCRIBE" ? 1 : c.Method == "UNSUBSCRIBE" ? -1 : 0);
        net.ShouldBe(1);
        _server.SubscribedStreams.ShouldBe([.. Seeded.Select(Stream), Stream("XRPUSDT")], ignoreOrder: true);
        _server.AcceptedConnections.ShouldBe(1);

        await _server.PushTradeAsync("ETHUSDT", 99, 3100m, Now.AddSeconds(-1));
        await WaitForTradeCountAsync(db, "ETHUSDT", 1);
    }

    [Fact]
    public async Task Reconnect_GapFill_RecoversMissedTrades_NoDuplicates()
    {
        await using var host = await StartAsync(db, _server);
        await WaitForSeededSubscriptionAsync(host);
        var ct = TestContext.Current.CancellationToken;
        var t0 = Now.AddMinutes(-5); // before any lead-in window, so only the reconnect gap-fill fetches them

        for (var i = 1; i <= 5; i++)
        {
            await _server.PushTradeAsync("BTCUSDT", i, 100m + i, t0.AddMilliseconds(i));
        }

        await WaitForTradeCountAsync(db, "BTCUSDT", 5);

        // Trades 6..10 happen on the exchange but never reach the socket, then the connection drops.
        for (var i = 6; i <= 10; i++)
        {
            _server.AddTrade("BTCUSDT", i, 100m + i, t0.AddMilliseconds(i));
        }

        _server.PauseAggTrades();
        await _server.DropAllConnectionsAsync();

        // Hold the gap-fill request until the new connection has streamed trades 11..1015, so that its first page
        // (6..1005, a full page) can only stop at the stream boundary, trade 11.
        await _server.WaitUntilAsync(s => s.ReceivedRequests.Any(r => r.Contains("&fromId=", StringComparison.Ordinal)), WaitTimeout, ct);
        for (var i = 11; i <= 1015; i++)
        {
            (await _server.PushTradeAsync("BTCUSDT", i, 100m + i, t0.AddMilliseconds(i))).ShouldBe(1);
        }

        await WaitForTradeCountAsync(db, "BTCUSDT", 1010);
        _server.ResumeAggTrades();
        await WaitForTradeCountAsync(db, "BTCUSDT", 1015);

        (await TradeIdsAsync(db, "BTCUSDT")).ShouldBe(Enumerable.Range(1, 1015).Select(i => (long)i));
        (await CandleTickCountAsync(db, "BTCUSDT")).ShouldBe(1015);
        await WaitForAsync(() => host.Logs.Entries.Any(e => e.EventName == "LogRecovered" && e.Message.Contains("5 BTCUSDT", StringComparison.Ordinal)), "gap-fill done");
        _server.ReceivedRequests.Where(r => r.Contains("&fromId=", StringComparison.Ordinal))
            .ShouldBe(["/api/v3/aggTrades?symbol=BTCUSDT&fromId=6&limit=1000"]); // one page; the other symbols had no gap

        // The lead-in ran once per symbol, when the first connection subscribed; a reconnect does not repeat it.
        _server.ReceivedRequests.Count(r => r.Contains("&startTime=", StringComparison.Ordinal)).ShouldBe(Seeded.Length);
        host.State.LastReconnectUtc.ShouldNotBeNull();
        _server.AcceptedConnections.ShouldBe(2);
    }

    [Fact]
    public async Task Restart_GapFillsTradesMissedWhileDown_ExactlyOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var t0 = Now.AddMinutes(-5);
        await using (var before = await StartAsync(db, _server))
        {
            await WaitForSeededSubscriptionAsync(before);
            for (var i = 1; i <= 5; i++)
            {
                await _server.PushTradeAsync("BTCUSDT", i, 100m + i, t0.AddMilliseconds(i));
                await _server.PushTradeAsync("ETHUSDT", 1000 + i, 3000m + i, t0.AddMilliseconds(i));
            }

            await WaitForTradeCountAsync(db, "BTCUSDT", 5);
            await WaitForTradeCountAsync(db, "ETHUSDT", 5);
        }

        // The process is gone (a restart or redeploy). Trades keep happening on the exchange; only REST has them.
        await _server.WaitUntilAsync(s => s.ActiveConnections == 0, WaitTimeout, ct);
        for (var i = 6; i <= 10; i++)
        {
            _server.AddTrade("BTCUSDT", i, 100m + i, t0.AddMilliseconds(i));
        }

        for (var i = 1006; i <= 1008; i++)
        {
            _server.AddTrade("ETHUSDT", i, 3000m + i, t0.AddMilliseconds(i));
        }

        // A new process: nothing in memory, so the gaps can only come from the database.
        await using var after = await StartAsync(db, _server);
        await WaitForSeededSubscriptionAsync(after);
        for (var i = 11; i <= 15; i++)
        {
            await _server.PushTradeAsync("BTCUSDT", i, 100m + i, t0.AddMilliseconds(i));
        }

        await _server.PushTradeAsync("ETHUSDT", 1009, 4009m, t0.AddMilliseconds(1009));

        await WaitForTradeCountAsync(db, "BTCUSDT", 15);
        await WaitForTradeCountAsync(db, "ETHUSDT", 9);
        (await TradeIdsAsync(db, "BTCUSDT")).ShouldBe(Enumerable.Range(1, 15).Select(i => (long)i));
        (await TradeIdsAsync(db, "ETHUSDT")).ShouldBe(Enumerable.Range(1001, 9).Select(i => (long)i));
        (await CandleTickCountAsync(db, "BTCUSDT")).ShouldBe(15);
        (await CandleTickCountAsync(db, "ETHUSDT")).ShouldBe(9);
        _server.ReceivedRequests.ShouldContain("/api/v3/aggTrades?symbol=BTCUSDT&fromId=6&limit=1000");
        _server.ReceivedRequests.ShouldContain("/api/v3/aggTrades?symbol=ETHUSDT&fromId=1006&limit=1000");
        _server.ReceivedRequests.ShouldNotContain(r => r.Contains("symbol=ADAUSDT&fromId", StringComparison.Ordinal)); // nothing stored, nothing to seed
    }

    [Fact]
    public async Task RestartAfterAnOutageOfDays_GapFillsFromTheLastStoredTrade()
    {
        var ct = TestContext.Current.CancellationToken;

        // The last trades were stored three days ago (longer than a day, well inside the replay window), in a chunk the
        // compression policy has compressed by now.
        var t0 = Now.AddDays(-3);
        await ExecuteSqlAsync(db, string.Create(
            CultureInfo.InvariantCulture,
            $"""
            INSERT INTO price_ticks (symbol_id, agg_trade_id, price, quantity, trade_time, ingested_at)
            SELECT s.id, i, 100 + i, 1, '{t0:O}'::timestamptz + i * interval '1 millisecond', now()
              FROM symbols s, generate_series(1, 5) AS i
             WHERE s.name = 'BTCUSDT'
            """));
        await ExecuteSqlAsync(db, "SELECT compress_chunk(c) FROM show_chunks('price_ticks') AS c");
        try
        {
            for (var i = 6; i <= 10; i++)
            {
                _server.AddTrade("BTCUSDT", i, 100m + i, t0.AddMilliseconds(i)); // while no instance ran
            }

            for (var i = 11; i <= 13; i++)
            {
                _server.AddTrade("BTCUSDT", i, 100m + i, Now.AddMinutes(-10).AddMilliseconds(i));
            }

            await using var host = await StartBtcOnlyAsync();
            for (var i = 14; i <= 18; i++)
            {
                await _server.PushTradeAsync("BTCUSDT", i, 100m + i, Now.AddSeconds(-1));
            }

            await WaitForTradeCountAsync(db, "BTCUSDT", 18);
            (await TradeIdsAsync(db, "BTCUSDT")).ShouldBe(Enumerable.Range(1, 18).Select(i => (long)i));
            (await CandleTickCountAsync(db, "BTCUSDT")).ShouldBe(13); // 1..5 were stored without candles
            _server.ReceivedRequests.ShouldContain("/api/v3/aggTrades?symbol=BTCUSDT&fromId=6&limit=1000");
        }
        finally
        {
            await ExecuteSqlAsync(db, "SELECT decompress_chunk(c, if_compressed => true) FROM show_chunks('price_ticks') AS c");
        }
    }

    [Fact]
    public async Task FirstStreamedMinute_IsCompletedFromRest()
    {
        var ct = TestContext.Current.CancellationToken;
        _server.PauseAggTrades();
        await using var host = await StartAsync(db, _server);
        await WaitForSeededSubscriptionAsync(host);

        // Nothing is stored yet, so each symbol fills from the start of the minute its stream started in.
        await _server.WaitUntilAsync(s => s.ReceivedRequests.Any(r => r.Contains("&startTime=", StringComparison.Ordinal)), WaitTimeout, ct);
        var startTime = long.Parse(
            Regex.Match(_server.ReceivedRequests.First(r => r.Contains("&startTime=", StringComparison.Ordinal)), @"startTime=(\d+)").Groups[1].Value,
            CultureInfo.InvariantCulture);
        var minute = DateTimeOffset.FromUnixTimeMilliseconds(startTime).UtcDateTime;
        minute.ShouldBe(Period.OneMinute.AlignDown(minute));
        minute.ShouldBeGreaterThan(Now.AddMinutes(-2));

        // Trades 1..5 of that minute traded before the subscription took effect: only REST has them.
        for (var i = 1; i <= 5; i++)
        {
            _server.AddTrade("BTCUSDT", i, 100m + i, minute.AddMilliseconds(i), quantity: 2m);
        }

        _server.ResumeAggTrades();
        for (var i = 6; i <= 10; i++)
        {
            await _server.PushTradeAsync("BTCUSDT", i, 100m + i, minute.AddMilliseconds(100 + i), quantity: 2m);
        }

        await WaitForTradeCountAsync(db, "BTCUSDT", 10);
        (await TradeIdsAsync(db, "BTCUSDT")).ShouldBe(Enumerable.Range(1, 10).Select(i => (long)i));

        // The minute's candle is the whole minute, as the exchange has it.
        await using var connection = new NpgsqlConnection(db.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(
            """
            SELECT c.open, c.high, c.low, c.close, c.first_trade_id, c.last_trade_id, c.tick_count, c.price_sum, c.volume, c.source
              FROM candles_1m c JOIN symbols s ON s.id = c.symbol_id
             WHERE s.name = 'BTCUSDT' AND c.bucket_start = $1
            """,
            connection);
        command.Parameters.Add(new NpgsqlParameter<DateTime> { TypedValue = minute, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.TimestampTz });
        await using var reader = await command.ExecuteReaderAsync(ct);
        (await reader.ReadAsync(ct)).ShouldBeTrue();
        (reader.GetDecimal(0), reader.GetDecimal(1), reader.GetDecimal(2), reader.GetDecimal(3)).ShouldBe((101m, 110m, 101m, 110m));
        (reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6)).ShouldBe((1L, 10L, 10L));
        reader.GetDecimal(7).ShouldBe(Enumerable.Range(101, 10).Sum(p => (decimal)p));
        reader.GetDecimal(8).ShouldBe(20m);
        reader.GetInt16(9).ShouldBe((short)1);
    }

    [Fact]
    public async Task ReactivatedSymbol_FirstStreamedMinuteIsCompletedAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(db, _server);
        await WaitForSeededSubscriptionAsync(host);
        await _server.WaitUntilAsync(s => s.ReceivedRequests.Count(r => r.Contains("&startTime=", StringComparison.Ordinal)) == Seeded.Length, WaitTimeout, ct);
        var eth = Symbol("ETHUSDT");
        (await host.Symbols.DeactivateAsync(eth, ct)).ShouldBeTrue();
        host.Signal.Notify();
        await _server.WaitUntilAsync(s => !s.SubscribedStreams.Contains(Stream("ETHUSDT")), WaitTimeout, ct);

        _server.PauseAggTrades();
        (await host.Symbols.AddOrReactivateAsync(eth, 20, ct)).Status.ShouldBe(SymbolAddStatus.Reactivated);
        host.Signal.Notify();
        static bool IsEthLeadIn(string r) => r.Contains("symbol=ETHUSDT&startTime=", StringComparison.Ordinal);
        await _server.WaitUntilAsync(s => s.ReceivedRequests.Count(IsEthLeadIn) == 2, WaitTimeout, ct);
        var minute = DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(
            Regex.Match(_server.ReceivedRequests.Last(IsEthLeadIn), @"startTime=(\d+)").Groups[1].Value,
            CultureInfo.InvariantCulture)).UtcDateTime;

        _server.AddTrade("ETHUSDT", 1, 3000m, minute.AddMilliseconds(1)); // before the new subscription
        _server.ResumeAggTrades();
        await _server.PushTradeAsync("ETHUSDT", 2, 3001m, minute.AddMilliseconds(2));

        await WaitForTradeCountAsync(db, "ETHUSDT", 2);
        (await TradeIdsAsync(db, "ETHUSDT")).ShouldBe([1L, 2L]);
    }

    [Fact]
    public async Task HandoverAndBack_FillsFromTheOtherLeadersLastStoredTrade()
    {
        var ct = TestContext.Current.CancellationToken;
        var t0 = Now.AddMinutes(-5);
        var logs = new LogCapture();
        await using var services = IngestionServices(logs);
        var aState = services.GetRequiredService<IngestionState>();
        var aLock = new YieldingLeaderLock(new PostgresLeaderLock(db.ConnectionString));
        using var a = NewWorker(services, aLock);
        await a.StartAsync(ct);
        try
        {
            // Instance A leads and stores 1..5.
            await _server.WaitUntilAsync(s => s.ActiveConnections == 1 && s.SubscribedStreams.SetEquals(Seeded.Select(Stream)), WaitTimeout, ct);
            await WaitForAsync(() => aState.Status == WsState.Connected, "A connected");
            for (var i = 1; i <= 5; i++)
            {
                await _server.PushTradeAsync("BTCUSDT", i, 100m + i, t0.AddMilliseconds(i));
            }

            await WaitForTradeCountAsync(db, "BTCUSDT", 5);

            // A loses leadership; instance B takes over and stores 6..10, then goes away.
            await using (var b = await StartAsync(db, _server))
            {
                aLock.Yield = true;
                await WaitForAsync(() => b.State.IsLeader && b.State.Status == WsState.Connected && !aState.IsLeader, "B leads");
                await _server.WaitUntilAsync(s => s.ActiveConnections == 1 && s.SubscribedStreams.SetEquals(Seeded.Select(Stream)), WaitTimeout, ct);
                for (var i = 6; i <= 10; i++)
                {
                    await _server.PushTradeAsync("BTCUSDT", i, 100m + i, t0.AddMilliseconds(i));
                }

                await WaitForTradeCountAsync(db, "BTCUSDT", 10);
            }

            await _server.WaitUntilAsync(s => s.ActiveConnections == 0, WaitTimeout, ct);
            for (var i = 11; i <= 15; i++)
            {
                _server.AddTrade("BTCUSDT", i, 100m + i, t0.AddMilliseconds(i)); // nobody leads
            }

            // A leads again. Its own watermark (5, from when it lost leadership) is older than what B stored.
            var requestsBefore = _server.ReceivedRequests.Count;
            aLock.Yield = false;
            await _server.WaitUntilAsync(s => s.ActiveConnections == 1 && s.SubscribedStreams.SetEquals(Seeded.Select(Stream)), WaitTimeout, ct);
            await WaitForAsync(() => aState.IsLeader && aState.Status == WsState.Connected, "A leads again");
            for (var i = 16; i <= 20; i++)
            {
                await _server.PushTradeAsync("BTCUSDT", i, 100m + i, t0.AddMilliseconds(i));
            }

            await WaitForTradeCountAsync(db, "BTCUSDT", 20);
            (await TradeIdsAsync(db, "BTCUSDT")).ShouldBe(Enumerable.Range(1, 20).Select(i => (long)i));
            (await CandleTickCountAsync(db, "BTCUSDT")).ShouldBe(20);
            _server.ReceivedRequests.Skip(requestsBefore).Where(r => r.Contains("symbol=BTCUSDT&fromId=", StringComparison.Ordinal))
                .ShouldBe(["/api/v3/aggTrades?symbol=BTCUSDT&fromId=11&limit=1000"]); // from B's last stored trade
        }
        finally
        {
            await a.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task SeededFill_ConnectionBreaksDuringIt_TradesAfterTheDeadConnectionAreRecovered()
    {
        var ct = TestContext.Current.CancellationToken;
        var t0 = Now.AddMinutes(-5);
        await ExecuteSqlAsync(db, string.Create(
            CultureInfo.InvariantCulture,
            $"""
            INSERT INTO price_ticks (symbol_id, agg_trade_id, price, quantity, trade_time, ingested_at)
            SELECT s.id, i, 100 + i, 1, '{t0:O}'::timestamptz + i * interval '1 millisecond', now()
              FROM symbols s, generate_series(1, 5) AS i
             WHERE s.name = 'BTCUSDT'
            """)); // stored before this process started
        for (var i = 6; i <= 10; i++)
        {
            _server.AddTrade("BTCUSDT", i, 100m + i, t0.AddMilliseconds(i)); // missed while no instance ran
        }

        // The seeded fill (fromId=6) is held while the first connection streams 11..15 and then breaks.
        _server.PauseAggTrades();
        await using var host = await StartBtcOnlyAsync();
        await _server.WaitUntilAsync(s => s.ReceivedRequests.Contains("/api/v3/aggTrades?symbol=BTCUSDT&fromId=6&limit=1000"), WaitTimeout, ct);
        for (var i = 11; i <= 15; i++)
        {
            await _server.PushTradeAsync("BTCUSDT", i, 100m + i, t0.AddMilliseconds(i));
        }

        await WaitForTradeCountAsync(db, "BTCUSDT", 10);
        _server.RefuseConnections = true;
        await _server.DropAllConnectionsAsync();
        await WaitForAsync(() => host.Logs.Count("LogConnectFailed") >= 1, "the dead connection retired, no new one yet");
        for (var i = 16; i <= 20; i++)
        {
            _server.AddTrade("BTCUSDT", i, 100m + i, t0.AddMilliseconds(i)); // after the dead connection's last trade
        }

        // The held fill completes up to the dead connection's first id (11) only; that must not close the gap.
        _server.ResumeAggTrades();
        await WaitForTradeCountAsync(db, "BTCUSDT", 15);
        _server.RefuseConnections = false;
        await _server.WaitUntilAsync(s => s.ActiveConnections == 1 && s.SubscribedStreams.SetEquals([Stream("BTCUSDT")]), WaitTimeout, ct);
        await WaitForAsync(() => host.State.Status == WsState.Connected, "connected again");
        for (var i = 21; i <= 25; i++)
        {
            await _server.PushTradeAsync("BTCUSDT", i, 100m + i, t0.AddMilliseconds(i));
        }

        await WaitForTradeCountAsync(db, "BTCUSDT", 25);
        (await TradeIdsAsync(db, "BTCUSDT")).ShouldBe(Enumerable.Range(1, 25).Select(i => (long)i));
        (await CandleTickCountAsync(db, "BTCUSDT")).ShouldBe(20); // 1..5 were stored without candles
    }

    [Theory]
    [InlineData("failed")]    // the reconnect's gap-fill fails
    [InlineData("capped")]    // the reconnect's gap-fill stops at the page cap (one page here)
    [InlineData("cancelled")] // the reconnect's gap-fill is still running when the session ends
    public async Task GapLeftOpen_ThenANewSessionInTheSameProcess_RecoversEveryTrade(string mode)
    {
        var capped = mode == "capped";
        var ct = TestContext.Current.CancellationToken;
        var t0 = Now.AddMinutes(-5);
        await ExecuteSqlAsync(db, "UPDATE symbols SET is_active = false WHERE name <> 'BTCUSDT'");
        var logs = new LogCapture();
        Action<IServiceCollection>? onePage = s => s.AddSingleton(sp => new GapFiller(
            sp.GetRequiredService<IAggTradeSource>(), TimeProvider.System, NullLogger<GapFiller>.Instance, sp.GetRequiredService<TickPulseMetrics>(), maxPages: 1));
        await using var services = IngestionServices(logs, capped ? onePage : null);
        var state = services.GetRequiredService<IngestionState>();
        var leaderLock = new YieldingLeaderLock(new PostgresLeaderLock(db.ConnectionString));
        using var worker = NewWorker(services, leaderLock);
        await worker.StartAsync(ct);
        async Task WaitConnectedAsync()
        {
            await _server.WaitUntilAsync(s => s.ActiveConnections == 1 && s.SubscribedStreams.SetEquals([Stream("BTCUSDT")]), WaitTimeout, ct);
            await WaitForAsync(() => state.IsLeader && state.Status == WsState.Connected, "connected");
        }

        try
        {
            await WaitConnectedAsync();
            for (var i = 1; i <= 5; i++)
            {
                await _server.PushTradeAsync("BTCUSDT", i, 100m + i, t0.AddMilliseconds(i));
            }

            await WaitForTradeCountAsync(db, "BTCUSDT", 5);

            // A reconnect gap after 5. Its fill fails, recovers only one page (6..1005) of 6..1010, or is held until the
            // session ends (then cancelled).
            var missedLast = capped ? 1010 : 10;
            if (mode == "failed")
            {
                _server.FailNextAggTrades(400, queryContains: "fromId=6&");
            }
            else if (mode == "cancelled")
            {
                _server.PauseAggTrades();
            }

            for (var i = 6; i <= missedLast; i++)
            {
                _server.AddTrade("BTCUSDT", i, 100m + i, t0.AddMilliseconds(i));
            }

            await _server.DropAllConnectionsAsync();
            await _server.WaitUntilAsync(s => s.ReceivedRequests.Contains("/api/v3/aggTrades?symbol=BTCUSDT&fromId=6&limit=1000"), WaitTimeout, ct);
            await WaitConnectedAsync();
            for (var i = missedLast + 1; i <= missedLast + 5; i++)
            {
                await _server.PushTradeAsync("BTCUSDT", i, 100m + i, t0.AddMilliseconds(i)); // stored above the open gap
            }

            await WaitForTradeCountAsync(db, "BTCUSDT", capped ? 1010 : 10);

            // The session ends in this same process and a new one starts: the stored maximum is this process's own
            // stream, above the open gap, so the seed must not start the fill from there.
            leaderLock.Yield = true;
            await WaitForAsync(() => logs.Count("LogSessionEnded") >= 1, "the session ended");
            if (mode == "cancelled")
            {
                _server.ResumeAggTrades(); // the held fill was cancelled with the session
            }

            leaderLock.Yield = false;
            await WaitConnectedAsync();
            for (var i = missedLast + 6; i <= missedLast + 10; i++)
            {
                await _server.PushTradeAsync("BTCUSDT", i, 100m + i, t0.AddMilliseconds(i));
            }

            await WaitForTradeCountAsync(db, "BTCUSDT", missedLast + 10);
            (await TradeIdsAsync(db, "BTCUSDT")).ShouldBe(Enumerable.Range(1, missedLast + 10).Select(i => (long)i));
            (await CandleTickCountAsync(db, "BTCUSDT")).ShouldBe(missedLast + 10);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task DiscardedRotationLink_StoredTradesAboveAGap_NextSessionStillFillsTheGap()
    {
        var ct = TestContext.Current.CancellationToken;
        var t0 = Now.AddMinutes(-5);
        await ExecuteSqlAsync(db, "UPDATE symbols SET is_active = false WHERE name <> 'BTCUSDT'");
        var logs = new LogCapture();
        await using var services = IngestionServices(logs, overrides: new Dictionary<string, string?> { ["Ingestion:RotationInterval"] = "00:00:02" });
        var state = services.GetRequiredService<IngestionState>();
        var leaderLock = new YieldingLeaderLock(new PostgresLeaderLock(db.ConnectionString));
        using var worker = NewWorker(services, leaderLock);
        await worker.StartAsync(ct);
        try
        {
            await _server.WaitUntilAsync(s => s.ActiveConnections == 1 && s.SubscribedStreams.SetEquals([Stream("BTCUSDT")]), WaitTimeout, ct);
            await WaitForAsync(() => state.Status == WsState.Connected, "connected");
            for (var i = 1; i <= 5; i++)
            {
                await _server.PushTradeAsync("BTCUSDT", i, 100m + i, t0.AddMilliseconds(i));
            }

            await WaitForTradeCountAsync(db, "BTCUSDT", 5);

            // The next rotation's SUBSCRIBE is applied but never acked, so its link streams but is discarded later.
            var commands = _server.ReceivedCommands.Count;
            _server.SuppressNextAck();
            await _server.WaitUntilAsync(s => s.ActiveConnections == 2 && s.ReceivedCommands.Count == commands + 1, WaitTimeout, ct);

            // Meanwhile the old connection breaks at 5. 6..10 reach neither; 11..15 reach only the rotation link.
            await _server.DropOldestConnectionAsync();
            for (var i = 6; i <= 10; i++)
            {
                _server.AddTrade("BTCUSDT", i, 100m + i, t0.AddMilliseconds(i));
            }

            for (var i = 11; i <= 15; i++)
            {
                (await _server.PushTradeAsync("BTCUSDT", i, 100m + i, t0.AddMilliseconds(i))).ShouldBe(1);
            }

            await WaitForTradeCountAsync(db, "BTCUSDT", 10); // 11..15 are stored above the gap

            // The rotation times out and discards its link; the reconnect's fill (from 6) is held and the session ends
            // before it completes.
            _server.PauseAggTrades();
            await WaitForAsync(() => logs.Count("LogRotationFailed") >= 1, "the rotation discarded its link", TimeSpan.FromSeconds(30));
            await _server.WaitUntilAsync(s => s.ReceivedRequests.Contains("/api/v3/aggTrades?symbol=BTCUSDT&fromId=6&limit=1000"), WaitTimeout, ct);
            leaderLock.Yield = true;
            await WaitForAsync(() => logs.Count("LogSessionEnded") >= 1, "the session ended");
            _server.ResumeAggTrades();

            // The new session's seed sees 15 stored, but the discarded link's 11..15 lie above the open gap.
            leaderLock.Yield = false;
            await _server.WaitUntilAsync(s => s.ActiveConnections == 1 && s.SubscribedStreams.SetEquals([Stream("BTCUSDT")]), WaitTimeout, ct);
            await WaitForAsync(() => state.IsLeader && state.Status == WsState.Connected, "leads again");
            for (var i = 16; i <= 20; i++)
            {
                await _server.PushTradeAsync("BTCUSDT", i, 100m + i, t0.AddMilliseconds(i));
            }

            await WaitForTradeCountAsync(db, "BTCUSDT", 20);
            (await TradeIdsAsync(db, "BTCUSDT")).ShouldBe(Enumerable.Range(1, 20).Select(i => (long)i));
            (await CandleTickCountAsync(db, "BTCUSDT")).ShouldBe(20);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task DroppedBatchOnALiveConnection_HoleClosesWithinTheSession()
    {
        var ct = TestContext.Current.CancellationToken;
        var t0 = Now.AddMinutes(-5);
        await ExecuteSqlAsync(db, "UPDATE symbols SET is_active = false WHERE name <> 'BTCUSDT'");
        var logs = new LogCapture();
        DroppingTickWriter? dropping = null;
        await using var services = IngestionServices(
            logs,
            overrides: new Dictionary<string, string?> { ["Ingestion:ReconcileInterval"] = "00:00:01" },
            replace: s => s.AddSingleton<ITickWriter>(sp => dropping = new DroppingTickWriter(
                new TickWriter(sp.GetRequiredKeyedService<NpgsqlDataSource>(DependencyInjection.TickWriterDataSourceKey), TimeProvider.System),
                dropFromId: 6)));
        var state = services.GetRequiredService<IngestionState>();
        using var worker = NewWorker(services, new PostgresLeaderLock(db.ConnectionString));
        await worker.StartAsync(ct);
        try
        {
            await _server.WaitUntilAsync(s => s.ActiveConnections == 1 && s.SubscribedStreams.SetEquals([Stream("BTCUSDT")]), WaitTimeout, ct);
            await WaitForAsync(() => state.Status == WsState.Connected, "connected");
            for (var i = 1; i <= 5; i++)
            {
                await _server.PushTradeAsync("BTCUSDT", i, 100m + i, t0.AddMilliseconds(i));
            }

            await WaitForTradeCountAsync(db, "BTCUSDT", 5);

            // The batch holding 6.. fails for good and is dropped; the stream goes on.
            for (var i = 6; i <= 10; i++)
            {
                await _server.PushTradeAsync("BTCUSDT", i, 100m + i, t0.AddMilliseconds(i));
            }

            await dropping.ShouldNotBeNull().Dropped.Task.WaitAsync(WaitTimeout, ct);
            dropping.Dropping = false;
            for (var i = 11; i <= 15; i++)
            {
                await _server.PushTradeAsync("BTCUSDT", i, 100m + i, t0.AddMilliseconds(i));
            }

            // A reconcile pass fills the hole on the same, healthy connection.
            await WaitForTradeCountAsync(db, "BTCUSDT", 15);
            (await TradeIdsAsync(db, "BTCUSDT")).ShouldBe(Enumerable.Range(1, 15).Select(i => (long)i));
            (await CandleTickCountAsync(db, "BTCUSDT")).ShouldBe(15);
            _server.AcceptedConnections.ShouldBe(1);
            await WaitForAsync(() => !services.GetRequiredService<GapFillWatermarks>().IsPending("BTCUSDT"), "the gap closed");
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task DroppedBatch_RestBehindTheStream_FillDoesNotCloseTheGap_UntilRestCatchesUp()
    {
        var ct = TestContext.Current.CancellationToken;
        var t0 = Now.AddMinutes(-5);
        await ExecuteSqlAsync(db, "UPDATE symbols SET is_active = false WHERE name <> 'BTCUSDT'");
        var logs = new LogCapture();
        DroppingTickWriter? dropping = null;
        await using var services = IngestionServices(
            logs,
            overrides: new Dictionary<string, string?> { ["Ingestion:ReconcileInterval"] = "00:00:01", ["Ingestion:RotationInterval"] = "00:00:02" },
            replace: s => s.AddSingleton<ITickWriter>(sp => dropping = new DroppingTickWriter(
                new TickWriter(sp.GetRequiredKeyedService<NpgsqlDataSource>(DependencyInjection.TickWriterDataSourceKey), TimeProvider.System),
                dropFromId: 6)));
        var state = services.GetRequiredService<IngestionState>();
        using var worker = NewWorker(services, new PostgresLeaderLock(db.ConnectionString));
        await worker.StartAsync(ct);
        try
        {
            await _server.WaitUntilAsync(s => s.ActiveConnections >= 1 && s.SubscribedStreams.SetEquals([Stream("BTCUSDT")]), WaitTimeout, ct);
            await WaitForAsync(() => state.Status == WsState.Connected, "connected");
            for (var i = 1; i <= 5; i++)
            {
                await _server.PushTradeAsync("BTCUSDT", i, 100m + i, t0.AddMilliseconds(i));
            }

            await WaitForTradeCountAsync(db, "BTCUSDT", 5);

            // Every batch holding 6.. is dropped: 6..10, which REST has, and 11..15, which only the stream has (REST lags).
            for (var i = 6; i <= 10; i++)
            {
                await _server.PushTradeAsync("BTCUSDT", i, 100m + i, t0.AddMilliseconds(i));
            }

            for (var i = 11; i <= 15; i++)
            {
                await _server.PushRawAsync(AggTradeJson("BTCUSDT", i, 100m + i, t0.AddMilliseconds(i)));
            }

            await WaitForAsync(() => dropping.ShouldNotBeNull().MaxSeen >= 15, "the writer dropped everything up to 15");
            dropping!.Dropping = false;

            // The fill from 6 is bounded by the stream's last id + 1 (16). REST's short page 6..10 is not the end of the
            // data, so the gap must stay open past 10.
            await _server.WaitUntilAsync(s => s.ReceivedRequests.Contains("/api/v3/aggTrades?symbol=BTCUSDT&fromId=6&limit=1000"), WaitTimeout, ct);
            await WaitForTradeCountAsync(db, "BTCUSDT", 10);

            // A rotation replaces the connection cleanly; REST then catches up and the new connection streams on.
            var rotations = logs.Count("LogRotated");
            await WaitForAsync(() => logs.Count("LogRotated") > rotations, "a rotation");
            for (var i = 11; i <= 15; i++)
            {
                _server.AddTrade("BTCUSDT", i, 100m + i, t0.AddMilliseconds(i));
            }

            await _server.WaitUntilAsync(s => s.ActiveConnections == 1, WaitTimeout, ct);
            for (var i = 16; i <= 20; i++)
            {
                await _server.PushTradeAsync("BTCUSDT", i, 100m + i, t0.AddMilliseconds(i));
            }

            await WaitForTradeCountAsync(db, "BTCUSDT", 20);
            (await TradeIdsAsync(db, "BTCUSDT")).ShouldBe(Enumerable.Range(1, 20).Select(i => (long)i));
            (await CandleTickCountAsync(db, "BTCUSDT")).ShouldBe(20);
            await WaitForAsync(() => !services.GetRequiredService<GapFillWatermarks>().IsPending("BTCUSDT"), "the gap closed");
            state.LastReconnectUtc.ShouldBeNull();
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Rotation_NewLinkSilent_RestBehind_GapStaysOpenUntilTheRetiredLinksLastId()
    {
        var ct = TestContext.Current.CancellationToken;
        var t0 = Now.AddMinutes(-5);
        await ExecuteSqlAsync(db, "UPDATE symbols SET is_active = false WHERE name <> 'BTCUSDT'");
        var logs = new LogCapture();
        DroppingTickWriter? dropping = null;
        await using var services = IngestionServices(
            logs,
            overrides: new Dictionary<string, string?> { ["Ingestion:ReconcileInterval"] = "00:00:01", ["Ingestion:RotationInterval"] = "00:00:03" },
            replace: s => s.AddSingleton<ITickWriter>(sp => dropping = new DroppingTickWriter(
                new TickWriter(sp.GetRequiredKeyedService<NpgsqlDataSource>(DependencyInjection.TickWriterDataSourceKey), TimeProvider.System),
                dropFromId: 6)));
        var state = services.GetRequiredService<IngestionState>();
        var watermarks = services.GetRequiredService<GapFillWatermarks>();
        using var worker = NewWorker(services, new PostgresLeaderLock(db.ConnectionString));
        await worker.StartAsync(ct);
        try
        {
            await _server.WaitUntilAsync(s => s.ActiveConnections >= 1 && s.SubscribedStreams.SetEquals([Stream("BTCUSDT")]), WaitTimeout, ct);
            await WaitForAsync(() => state.Status == WsState.Connected, "connected");
            for (var i = 1; i <= 5; i++)
            {
                await _server.PushTradeAsync("BTCUSDT", i, 100m + i, t0.AddMilliseconds(i));
            }

            await WaitForTradeCountAsync(db, "BTCUSDT", 5);

            // The link streams 6..15 and every batch holding them is dropped. REST has 6..10 only (it lags).
            for (var i = 6; i <= 10; i++)
            {
                await _server.PushTradeAsync("BTCUSDT", i, 100m + i, t0.AddMilliseconds(i));
            }

            for (var i = 11; i <= 15; i++)
            {
                await _server.PushRawAsync(AggTradeJson("BTCUSDT", i, 100m + i, t0.AddMilliseconds(i)));
            }

            await WaitForAsync(() => dropping.ShouldNotBeNull().MaxSeen >= 15, "the writer dropped everything up to 15");
            dropping!.Dropping = false;
            await WaitForTradeCountAsync(db, "BTCUSDT", 10); // the fill on that link got 6..10 and stays open (bound 16)

            // A rotation replaces the link. The new one streams nothing for the symbol, so it has no boundary of its own;
            // the retired link delivered up to 15, so a lagging REST page from 11 must not close the gap.
            static bool From11(string r) => r == "/api/v3/aggTrades?symbol=BTCUSDT&fromId=11&limit=1000";
            var rotations = logs.Count("LogRotated");
            await WaitForAsync(() => logs.Count("LogRotated") > rotations, "a rotation");
            var before = _server.ReceivedRequests.Count;
            await _server.WaitUntilAsync(s => s.ReceivedRequests.Skip(before).Count(From11) >= 3, WaitTimeout, ct);
            watermarks.IsPending("BTCUSDT").ShouldBeTrue();

            // REST catches up: 11..15 are recovered with nothing more on the stream.
            for (var i = 11; i <= 15; i++)
            {
                _server.AddTrade("BTCUSDT", i, 100m + i, t0.AddMilliseconds(i));
            }

            await WaitForTradeCountAsync(db, "BTCUSDT", 15);
            await WaitForAsync(() => !watermarks.IsPending("BTCUSDT"), "the gap closed");
            for (var i = 16; i <= 20; i++)
            {
                await _server.PushTradeAsync("BTCUSDT", i, 100m + i, t0.AddMilliseconds(i));
            }

            await WaitForTradeCountAsync(db, "BTCUSDT", 20);
            (await TradeIdsAsync(db, "BTCUSDT")).ShouldBe(Enumerable.Range(1, 20).Select(i => (long)i));
            (await CandleTickCountAsync(db, "BTCUSDT")).ShouldBe(20);
            state.LastReconnectUtc.ShouldBeNull();
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task LeadInFailed_IsRetriedOnTheNextConnection()
    {
        var ct = TestContext.Current.CancellationToken;
        _server.FailNextAggTrades(400); // a permanent error: the lookup gives up at once
        await using var host = await StartBtcOnlyAsync();
        static bool IsLeadIn(string r) => r.Contains("symbol=BTCUSDT&startTime=", StringComparison.Ordinal);
        await _server.WaitUntilAsync(s => s.ReceivedRequests.Count(IsLeadIn) == 1, WaitTimeout, ct);
        var startTime = LeadInStart(_server.ReceivedRequests.Single(IsLeadIn));
        var minute = DateTimeOffset.FromUnixTimeMilliseconds(startTime).UtcDateTime;

        _server.AddTrade("BTCUSDT", 1, 100m, minute.AddMilliseconds(1)); // before the subscription: REST only
        await _server.PushTradeAsync("BTCUSDT", 2, 101m, minute.AddMilliseconds(2));
        await WaitForTradeCountAsync(db, "BTCUSDT", 1);

        // The failed lead-in is still due: the next connection tries it again, for the same minute.
        await _server.DropAllConnectionsAsync();
        await WaitForTradeCountAsync(db, "BTCUSDT", 2);
        (await TradeIdsAsync(db, "BTCUSDT")).ShouldBe([1L, 2L]);
        _server.ReceivedRequests.Where(IsLeadIn).Select(LeadInStart).ShouldBe([startTime, startTime]);
    }

    [Fact]
    public async Task Rotation_OverlapsWithoutLossOrDuplicates()
    {
        await using var host = await StartAsync(db, _server, new Dictionary<string, string?> { ["Ingestion:RotationInterval"] = "00:00:02" });
        await WaitForSeededSubscriptionAsync(host);
        var t0 = Now.AddMinutes(-10); // before the lead-in window

        // A concurrent pusher streams trades as fast as the server sends them, across at least two rotations and until
        // at least one trade arrived while both connections were subscribed (the overlap window is short; under load a
        // single rotation can pass without a push landing in it, so further rotations every 2 s give it more chances).
        using var stop = new CancellationTokenSource();
        var sentToBoth = 0;
        var pusher = Task.Run(async () =>
        {
            long id = 0;
            while (!stop.IsCancellationRequested)
            {
                id++;
                if (await _server.PushTradeAsync("BTCUSDT", id, id, t0.AddMilliseconds(id)) == 2)
                {
                    Interlocked.Increment(ref sentToBoth);
                }
            }

            return (Count: id, ToBoth: Volatile.Read(ref sentToBoth));
        });
        await WaitForAsync(
            () => (host.Logs.Count("LogRotated") >= 2 && Volatile.Read(ref sentToBoth) > 0) || pusher.IsCompleted,
            "two rotations, one with a trade sent to both connections",
            TimeSpan.FromSeconds(30));
        await stop.CancelAsync();
        var (count, toBoth) = await pusher;

        await WaitForTradeCountAsync(db, "BTCUSDT", (int)count, TimeSpan.FromSeconds(60));
        await _server.WaitUntilAsync(s => s.ActiveConnections == 1, WaitTimeout, TestContext.Current.CancellationToken);

        toBoth.ShouldBeGreaterThan(0);                          // the overlap carried duplicates
        (await TradeIdsAsync(db, "BTCUSDT")).ShouldBe(LongRange(1, count)); // none lost, none stored twice
        (await CandleTickCountAsync(db, "BTCUSDT")).ShouldBe(count);
        _server.PeakConnections.ShouldBe(2);                  // make-before-break: the old one closes after the new one is up
        _server.ReceivedRequests.Count.ShouldBe(Seeded.Length); // a clean rotation needs no gap-fill: only the
        _server.ReceivedRequests.ShouldAllBe(r => r.Contains("&startTime=", StringComparison.Ordinal)); // first lead-ins
        host.State.LastReconnectUtc.ShouldBeNull();             // and is not a reconnect
    }

    [Fact]
    public async Task Rotation_OverAStalledConnection_FillsTheTradesItMissed()
    {
        var ct = TestContext.Current.CancellationToken;
        var t0 = Now.AddMinutes(-5);
        await ExecuteSqlAsync(db, "UPDATE symbols SET is_active = false WHERE name <> 'BTCUSDT'");
        await using var host = await StartAsync(db, _server, new Dictionary<string, string?>
        {
            ["Ingestion:RotationInterval"] = "00:00:02",
            ["Ingestion:ReconcileInterval"] = "00:00:01",
        });
        await _server.WaitUntilAsync(s => s.ActiveConnections == 1 && s.SubscribedStreams.SetEquals([Stream("BTCUSDT")]), WaitTimeout, ct);
        await WaitForAsync(() => host.State.Status == WsState.Connected, "connected");
        for (var i = 1; i <= 5; i++)
        {
            await _server.PushTradeAsync("BTCUSDT", i, 100m + i, t0.AddMilliseconds(i));
        }

        await WaitForTradeCountAsync(db, "BTCUSDT", 5);

        // The connection stalls: 6..10 happen on the exchange but never arrive on it. It is still open, so the next
        // rotation replaces it cleanly (no reconnect), and only the new connection streams 11..15.
        for (var i = 6; i <= 10; i++)
        {
            _server.AddTrade("BTCUSDT", i, 100m + i, t0.AddMilliseconds(i));
        }

        var rotations = host.Logs.Count("LogRotated");
        await WaitForAsync(() => host.Logs.Count("LogRotated") > rotations, "a rotation");
        await _server.WaitUntilAsync(s => s.ActiveConnections == 1, WaitTimeout, ct); // the old connection is gone
        for (var i = 11; i <= 15; i++)
        {
            await _server.PushTradeAsync("BTCUSDT", i, 100m + i, t0.AddMilliseconds(i));
        }

        // The new connection's first trade lies above the old one's last + 1: the trades between are fetched.
        await WaitForTradeCountAsync(db, "BTCUSDT", 15);
        (await TradeIdsAsync(db, "BTCUSDT")).ShouldBe(Enumerable.Range(1, 15).Select(i => (long)i));
        (await CandleTickCountAsync(db, "BTCUSDT")).ShouldBe(15);
        _server.ReceivedRequests.Where(r => r.Contains("&fromId=", StringComparison.Ordinal))
            .ShouldBe(["/api/v3/aggTrades?symbol=BTCUSDT&fromId=6&limit=1000"]);
        host.State.LastReconnectUtc.ShouldBeNull();
    }

    [Fact]
    public async Task Rotation_NewConnectionSubscribeRejected_KeepsTheOldOne()
    {
        await using var host = await StartAsync(db, _server, new Dictionary<string, string?> { ["Ingestion:RotationInterval"] = "00:00:02" });
        await WaitForSeededSubscriptionAsync(host);
        var since = host.State.ConnectedSinceUtc.ShouldNotBeNull();

        // Nothing else is sent before it, so the next command is the rotation's SUBSCRIBE on the new connection.
        _server.RejectNextCommand(-1, "Internal error");
        await WaitForAsync(() => host.Logs.Count("LogRotationFailed") == 1, "the rotation refused");

        host.State.ConnectedSinceUtc.ShouldBe(since);
        await WaitForAsync(() => host.State.NextRotationUtc > since + TimeSpan.FromSeconds(2), "the retry time published");
        host.Logs.Count("LogRotated").ShouldBe(0);
        await _server.WaitUntilAsync(s => s.ActiveConnections == 1 && s.SubscribedStreams.SetEquals(Seeded.Select(Stream)), WaitTimeout, TestContext.Current.CancellationToken);
        await _server.PushTradeAsync("BTCUSDT", 1, 100m, Now.AddSeconds(-2));
        await WaitForTradeCountAsync(db, "BTCUSDT", 1); // the old connection still streams

        // The next attempt succeeds.
        await WaitForAsync(() => host.Logs.Count("LogRotated") >= 1, "the retried rotation");
        host.State.ConnectedSinceUtc.ShouldNotBe(since);
        await _server.WaitUntilAsync(s => s.ActiveConnections == 1, WaitTimeout, TestContext.Current.CancellationToken);
        await _server.PushTradeAsync("BTCUSDT", 2, 101m, Now.AddSeconds(-1));
        await WaitForTradeCountAsync(db, "BTCUSDT", 2);
        (await TradeIdsAsync(db, "BTCUSDT")).ShouldBe([1L, 2L]);
        _server.AcceptedConnections.ShouldBeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public async Task SecondInstance_StaysStandby()
    {
        await using var first = await StartAsync(db, _server);
        await WaitForSeededSubscriptionAsync(first);
        await WaitForAsync(() => first.State.IsLeader && first.State.Status == WsState.Connected, "first instance leads");

        await using var second = await StartAsync(db, _server);
        await WaitForAsync(() => second.Logs.Count("LogLeaderLockBusy") >= 3, "second instance tried the lock three times");

        second.State.IsLeader.ShouldBeFalse();
        second.State.Status.ShouldBe(WsState.Standby);
        second.State.ConnectedSinceUtc.ShouldBeNull();
        first.State.Status.ShouldBe(WsState.Connected);
        _server.AcceptedConnections.ShouldBe(1);
        _server.PeakConnections.ShouldBe(1);

        // Failover: once the leader stops, the standby takes the lock and connects.
        await first.StopAsync();
        await WaitForAsync(() => second.State.Status == WsState.Connected, "second instance connected");
        await _server.WaitUntilAsync(s => s.ActiveConnections == 1 && s.SubscribedStreams.SetEquals(Seeded.Select(Stream)), WaitTimeout, TestContext.Current.CancellationToken);
        first.State.IsLeader.ShouldBeFalse();
        second.State.IsLeader.ShouldBeTrue();

        await _server.PushTradeAsync("BTCUSDT", 1, 100m, Now.AddSeconds(-1));
        await WaitForTradeCountAsync(db, "BTCUSDT", 1);
    }

    [Fact]
    public async Task MalformedMessage_IsSkipped()
    {
        await using var host = await StartAsync(db, _server);
        await WaitForSeededSubscriptionAsync(host);
        var t = Now.AddSeconds(-1);

        await _server.PushRawAsync("{not json");
        await _server.PushRawAsync("""{"stream":"btcusdt@aggTrade","data":{"e":"aggTrade","s":"BTCUSDT","a":"x","p":"1"}}""");
        await _server.PushRawAsync("""{"stream":"btcusdt@aggTrade","data":{"e":"aggTrade","s":"BTCUSDT","a":5,"p":"abc","q":"1","T":1}}""");
        await _server.PushRawAsync("[]");
        await _server.PushTradeAsync("BTCUSDT", 1, 100m, t);

        await WaitForTradeCountAsync(db, "BTCUSDT", 1);
        (await TradeIdsAsync(db, "BTCUSDT")).ShouldBe([1L]);
        host.State.Status.ShouldBe(WsState.Connected);
        _server.AcceptedConnections.ShouldBe(1);
        _server.ActiveConnections.ShouldBe(1);
    }

    [Fact]
    public async Task LeaderConnectionLost_StopsIngestion_ThenReacquires()
    {
        await using var host = await StartAsync(db, _server);
        await WaitForSeededSubscriptionAsync(host);
        await _server.PushTradeAsync("BTCUSDT", 1, 100m, Now.AddSeconds(-2));
        await WaitForTradeCountAsync(db, "BTCUSDT", 1);

        // Kill the session that holds the advisory lock, as a database failover or network cut would.
        await using (var connection = new NpgsqlConnection(db.ConnectionString))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var kill = new NpgsqlCommand(
                """
                SELECT count(pg_terminate_backend(pid))::integer
                  FROM pg_locks
                 WHERE locktype = 'advisory' AND classid = 0 AND objid::bigint = $1 AND granted
                """,
                connection);
            kill.Parameters.Add(new NpgsqlParameter<long> { TypedValue = PostgresLeaderLock.LockKey });
            ((int)(await kill.ExecuteScalarAsync(TestContext.Current.CancellationToken))!).ShouldBe(1);
        }

        await WaitForAsync(() => host.Logs.Count("LogLeadershipLost") == 1, "leadership loss noticed");
        await _server.WaitUntilAsync(s => s.AcceptedConnections == 2 && s.SubscribedStreams.SetEquals(Seeded.Select(Stream)), WaitTimeout, TestContext.Current.CancellationToken);
        await WaitForAsync(() => host.State.IsLeader && host.State.Status == WsState.Connected, "leader again");

        await _server.PushTradeAsync("BTCUSDT", 2, 101m, Now.AddSeconds(-1));
        await WaitForTradeCountAsync(db, "BTCUSDT", 2);
        _server.ActiveConnections.ShouldBe(1);
    }

    [Fact]
    public async Task UnexpectedError_LogsCritical_RestartsAndRecovers()
    {
        var ct = TestContext.Current.CancellationToken;
        var logs = new LogCapture();
        await using var provider = IngestionServices(logs);
        var state = provider.GetRequiredService<IngestionState>();

        // The worker as AddTickPulseIngestion builds it, but with a lock whose first acquire fails unexpectedly.
        using var worker = NewWorker(provider, new ThrowOnceLeaderLock(new PostgresLeaderLock(db.ConnectionString)));
        await worker.StartAsync(ct);
        try
        {
            await WaitForAsync(() => logs.Count("LogUnexpectedError") == 1, "the unexpected error logged");
            logs.Entries.Where(e => e.EventName == "LogUnexpectedError").ShouldHaveSingleItem().Level.ShouldBe(LogLevel.Critical);
            worker.ExecuteTask.ShouldNotBeNull().IsCompleted.ShouldBeFalse();

            // The next attempt takes the lock and ingests as usual.
            await _server.WaitUntilAsync(s => s.ActiveConnections == 1 && s.SubscribedStreams.SetEquals(Seeded.Select(Stream)), WaitTimeout, ct);
            await WaitForAsync(() => state.Status == WsState.Connected, "the worker connected");
            await _server.PushTradeAsync("BTCUSDT", 1, 100m, Now.AddSeconds(-1));
            await WaitForTradeCountAsync(db, "BTCUSDT", 1);

            worker.ExecuteTask.IsCompleted.ShouldBeFalse();
            logs.Count("LogUnexpectedError").ShouldBe(1);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData(10, 1, 10)]
    [InlineData(10, 2, 20)]
    [InlineData(10, 3, 30)]
    [InlineData(10, 50, 30)]
    [InlineData(60, 4, 60)]
    public void RestartDelay_DoublesUpToCap(int retrySeconds, int failures, int expectedSeconds) =>
        IngestionWorker.RestartDelay(TimeSpan.FromSeconds(retrySeconds), failures).ShouldBe(TimeSpan.FromSeconds(expectedSeconds));

    private static SymbolName Symbol(string raw) => SymbolName.TryCreate(raw, out var name) ? name : throw new ArgumentException(raw);

    // The worker exactly as AddTickPulseIngestion builds it, except for the leader lock.
    private static IngestionWorker NewWorker(IServiceProvider provider, ILeaderLock leaderLock) => new(
        leaderLock,
        provider.GetRequiredService<IStreamConnectionFactory>(),
        provider.GetRequiredService<SymbolReconciler>(),
        provider.GetRequiredService<TickBatchWriter>(),
        provider.GetRequiredService<GapFiller>(),
        provider.GetRequiredService<SymbolIdCache>(),
        provider.GetRequiredService<GapFillWatermarks>(),
        provider.GetRequiredService<IMarketDataQueries>(),
        provider.GetRequiredService<ISymbolChangeSignal>(),
        provider.GetRequiredService<IngestionState>(),
        provider.GetRequiredService<IOptions<IngestionOptions>>(),
        TimeProvider.System,
        provider.GetRequiredService<ILoggerFactory>(),
        provider.GetRequiredService<TickPulseMetrics>());

    private static async Task ExecuteSqlAsync(TimescaleFixture db, string sql)
    {
        await using var connection = new NpgsqlConnection(db.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static long LeadInStart(string request) =>
        long.Parse(Regex.Match(request, @"startTime=(\d+)").Groups[1].Value, CultureInfo.InvariantCulture);

    // A combined-stream aggTrade message as the fake server would send it, for a trade that is not in its REST data.
    private static string AggTradeJson(string symbol, long id, decimal price, DateTime t)
    {
        var ms = new DateTimeOffset(t).ToUnixTimeMilliseconds();
        return string.Create(
            CultureInfo.InvariantCulture,
            $$$"""{"stream":"{{{Stream(symbol)}}}","data":{"e":"aggTrade","E":{{{ms}}},"s":"{{{symbol}}}","a":{{{id}}},"p":"{{{price:0.00000000}}}","q":"1.00000000","f":{{{id}}},"l":{{{id}}},"T":{{{ms}}},"m":false,"M":true}}""");
    }

    // One process's ingestion services, as the API registers them (its own symbol cache, watermarks and state).
    private ServiceProvider IngestionServices(
        LogCapture logs,
        Action<IServiceCollection>? configure = null,
        IDictionary<string, string?>? overrides = null,
        Action<IServiceCollection>? replace = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:TickPulse"] = db.ConnectionString,
            ["Binance:WsBaseUrl"] = _server.WsBaseUrl.ToString(),
            ["Binance:RestBaseUrl"] = _server.RestBaseUrl.ToString(),
            ["Ingestion:BatchMaxDelay"] = "00:00:00.020",
            ["Ingestion:LeaderRetryInterval"] = "00:00:00.200",
            ["Ingestion:LeaderCheckInterval"] = "00:00:00.200",
        };
        foreach (var (key, value) in overrides ?? new Dictionary<string, string?>())
        {
            settings[key] = value;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddProvider(logs).SetMinimumLevel(LogLevel.Debug));
        configure?.Invoke(services); // first, so the TryAdd registrations below keep it
        services.AddTickPulseInfrastructure(configuration);
        services.AddTickPulseIngestion(configuration);
        replace?.Invoke(services); // last, so it wins over plain Add registrations
        return services.BuildServiceProvider();
    }

    // Only BTCUSDT stays active, so its gap-fill is the only REST traffic.
    private async Task<IngestionTestHost> StartBtcOnlyAsync()
    {
        await ExecuteSqlAsync(db, "UPDATE symbols SET is_active = false WHERE name <> 'BTCUSDT'");
        var host = await StartAsync(db, _server);
        await _server.WaitUntilAsync(
            s => s.ActiveConnections == 1 && s.SubscribedStreams.SetEquals([Stream("BTCUSDT")]), WaitTimeout, TestContext.Current.CancellationToken);
        await WaitForAsync(() => host.State.Status == WsState.Connected, "the worker connected");
        return host;
    }

    private static IEnumerable<long> LongRange(long from, long to)
    {
        for (var i = from; i <= to; i++)
        {
            yield return i;
        }
    }

    private static (string Method, string[] Params) Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        return (
            root.GetProperty("method").GetString()!,
            [.. root.GetProperty("params").EnumerateArray().Select(e => e.GetString()!)]);
    }

    /// <summary>A real lock whose first <see cref="TryAcquireAsync"/> throws an unexpected <see cref="InvalidOperationException"/>.</summary>
    private sealed class ThrowOnceLeaderLock(PostgresLeaderLock inner) : ILeaderLock
    {
        private int _calls;

        public Task<bool> TryAcquireAsync(CancellationToken cancellationToken) =>
            Interlocked.Increment(ref _calls) == 1
                ? throw new InvalidOperationException("Injected unexpected failure.")
                : inner.TryAcquireAsync(cancellationToken);

        public Task<bool> CheckAsync(CancellationToken cancellationToken) => inner.CheckAsync(cancellationToken);

        public Task ReleaseAsync() => inner.ReleaseAsync();

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    /// <summary>
    /// The real writer, except that while <see cref="Dropping"/> every batch holding an id from <paramref name="dropFromId"/>
    /// on fails for good.
    /// </summary>
    private sealed class DroppingTickWriter(ITickWriter inner, long dropFromId) : ITickWriter
    {
        private long _maxSeen;

        public volatile bool Dropping = true;

        public TaskCompletionSource Dropped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>The highest trade id in any batch handed to the writer so far, dropped or not.</summary>
        public long MaxSeen => Volatile.Read(ref _maxSeen);

        public Task<int> WriteBatchAsync(IReadOnlyList<Tick> ticks, CancellationToken ct) =>
            ShouldDrop(ticks) ? Fail() : inner.WriteBatchAsync(ticks, ct);

        public Task<int> WriteBatchAsync(IReadOnlyList<Tick> ticks, IDictionary<int, int> insertedPerSymbol, CancellationToken ct) =>
            ShouldDrop(ticks) ? Fail() : inner.WriteBatchAsync(ticks, insertedPerSymbol, ct);

        private bool ShouldDrop(IReadOnlyList<Tick> ticks)
        {
            var max = ticks.Max(t => t.AggTradeId);
            long seen;
            while ((seen = Volatile.Read(ref _maxSeen)) < max && Interlocked.CompareExchange(ref _maxSeen, max, seen) != seen)
            {
            }

            return Dropping && ticks.Any(t => t.AggTradeId >= dropFromId);
        }

        private Task<int> Fail()
        {
            Dropped.TrySetResult();
            return Task.FromException<int>(new InvalidOperationException("Injected permanent write failure."));
        }
    }

    /// <summary>A real lock that can be made to give leadership up and refuse it until told otherwise.</summary>
    private sealed class YieldingLeaderLock(PostgresLeaderLock inner) : ILeaderLock
    {
        private volatile bool _yield;

        public bool Yield
        {
            get => _yield;
            set => _yield = value;
        }

        public Task<bool> TryAcquireAsync(CancellationToken cancellationToken) =>
            _yield ? Task.FromResult(false) : inner.TryAcquireAsync(cancellationToken);

        public Task<bool> CheckAsync(CancellationToken cancellationToken) =>
            _yield ? Task.FromResult(false) : inner.CheckAsync(cancellationToken);

        public Task ReleaseAsync() => inner.ReleaseAsync();

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    // The server applies the SUBSCRIBE before the worker reads its ack, so wait for the worker's side as well.
    private async Task WaitForSeededSubscriptionAsync(IngestionTestHost host)
    {
        await _server.WaitUntilAsync(s => s.ActiveConnections == 1 && s.SubscribedStreams.SetEquals(Seeded.Select(Stream)), WaitTimeout, TestContext.Current.CancellationToken);
        await WaitForAsync(() => host.State.Status == WsState.Connected, "the worker connected");
    }
}
