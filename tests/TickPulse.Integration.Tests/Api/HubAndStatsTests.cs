using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Threading.Channels;
using System.Xml.Linq;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using TickPulse.Api.Hosting;
using TickPulse.Api.Hubs;
using TickPulse.Application.Abstractions;
using TickPulse.Application.Ingestion;
using TickPulse.Integration.Tests.Fixtures;
using TickPulse.Integration.Tests.Ingestion;
using static TickPulse.Integration.Tests.Api.ProblemReader;

namespace TickPulse.Integration.Tests.Api;

/// <summary>
/// <c>GET /api/system/stats</c> and the market hub. The class fixture runs on a frozen fake clock without workers;
/// tests that need time to pass, live ingestion, other settings or test doubles start their own <see cref="ApiFactory"/>.
/// </summary>
/// <remarks>
/// Advancing the class fixture's clock (only <see cref="Stats_ReturnsIngestionState"/> does, to expire the cached
/// counts) is safe: the fixture belongs to this class alone, every test reads its times relative to
/// <see cref="ApiFactory.Clock"/>, and its broadcaster has no hub clients to send to.
/// </remarks>
public sealed class HubAndStatsTests(ApiFactory api) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private static readonly XNamespace Schema = "https://tickpulse.dev/schema/v1";

    private static readonly string[] StatsNames =
    [
        "wsStatus", "isLeader", "ticksPerSecond", "ingestLagMs", "storedTicksApprox", "uptimeSeconds",
        "connectedSince", "lastReconnect", "nextRotation", "activeSymbols", "version",
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await api.Database.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // ---- GET /api/system/stats -----------------------------------------------------------------------------------

    [Fact]
    public async Task Stats_ReturnsIngestionState()
    {
        // Past the cache lifetime, so the counts below are read now.
        api.Clock.Advance(new SystemStatsOptions().CacheTtl);
        var now = api.Clock.GetUtcNow().UtcDateTime;
        var state = api.Services.GetRequiredService<IngestionState>();
        state.SetConnected(now.AddMinutes(-10), now.AddHours(22));
        state.MarkReconnected(now.AddMinutes(-10));

        // The clock is frozen from here on, so the rate does not decay: 50 ticks over the 10 s window are 5 ticks/s.
        state.RecordBatch(50, 12.5, []);
        var storedTicks = await api.Services.GetRequiredService<IMarketDataQueries>().ApproximateTickCountAsync(Ct);
        using var client = api.CreateClient();

        using var response = await client.SendAsync(Get("/api/system/stats", accept: null), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        MediaType(response).ShouldBe("application/json");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var root = json.RootElement;
        root.EnumerateObject().Select(p => p.Name).ShouldBe(StatsNames);
        root.GetProperty("wsStatus").GetString().ShouldBe("Connected");
        root.GetProperty("isLeader").GetBoolean().ShouldBeTrue();
        root.GetProperty("ticksPerSecond").GetDouble().ShouldBe(5d, tolerance: 1e-9);
        root.GetProperty("ingestLagMs").GetDouble().ShouldBe(12.5d);
        root.GetProperty("storedTicksApprox").GetInt64().ShouldBe(storedTicks);
        root.GetProperty("uptimeSeconds").GetInt64().ShouldBeGreaterThanOrEqualTo(0);
        root.GetProperty("connectedSince").GetString().ShouldBe(Iso(now.AddMinutes(-10)));
        root.GetProperty("lastReconnect").GetString().ShouldBe(Iso(now.AddMinutes(-10)));
        root.GetProperty("nextRotation").GetString().ShouldBe(Iso(now.AddHours(22)));
        root.GetProperty("activeSymbols").GetInt32().ShouldBe(3);
        var version = root.GetProperty("version").GetString();
        version.ShouldBe(SystemStatsProvider.ShortVersion(InformationalVersion()));
        version!.ShouldMatch(@"^[0-9A-Za-z.\-]+(\+[0-9A-Za-z.\-]{1,7})?$");
    }

    [Fact]
    public async Task Stats_Standby_HasNullTimestamps()
    {
        var state = api.Services.GetRequiredService<IngestionState>();
        state.SetStandby();
        using var client = api.CreateClient();

        using var response = await client.SendAsync(Get("/api/system/stats", accept: null), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var root = json.RootElement;
        root.GetProperty("wsStatus").GetString().ShouldBe("Standby");
        root.GetProperty("isLeader").GetBoolean().ShouldBeFalse();
        root.GetProperty("connectedSince").ValueKind.ShouldBe(JsonValueKind.Null);
        root.GetProperty("nextRotation").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Stats_UptimeFollowsTheClock()
    {
        // Its own host, so moving the clock 90 s touches no other test.
        await using var own = new ApiFactory(api.Database);
        await own.InitializeAsync();
        using var client = own.CreateClient();
        var before = await UptimeAsync(client);

        own.Clock.Advance(TimeSpan.FromSeconds(90));
        var after = await UptimeAsync(client);

        (after - before).ShouldBe(90);
    }

    [Fact]
    public async Task Stats_Xml_UsesTheJsonNames()
    {
        var now = api.Clock.GetUtcNow().UtcDateTime;
        api.Services.GetRequiredService<IngestionState>().SetConnected(now, now.AddHours(23));
        using var client = api.CreateClient();

        using var response = await client.SendAsync(Get("/api/system/stats", "application/xml"), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        MediaType(response).ShouldBe("application/xml");
        var root = XDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).Root!;
        root.Name.ShouldBe(Schema + "systemStats");
        root.Elements().Select(e => e.Name.LocalName).ShouldBe(StatsNames);
        root.Element(Schema + "wsStatus")!.Value.ShouldBe("Connected");
        root.Element(Schema + "connectedSince")!.Value.ShouldBe(Iso(now));
    }

    [Fact]
    public async Task Stats_DatabaseDown_Returns200WithLastKnownCounts()
    {
        var counts = new ControllableCounts { Behaviour = _ => throw new InvalidOperationException("The database is unreachable.") };
        await using var own = new ApiFactory(api.Database).WithServices(s => s.Replace(ServiceDescriptor.Singleton<IStoredCounts>(counts)));
        await own.InitializeAsync();
        var ttl = new SystemStatsOptions().CacheTtl;
        var now = own.Clock.GetUtcNow().UtcDateTime;
        var state = own.Services.GetRequiredService<IngestionState>();
        state.SetConnected(now, now.AddHours(23));
        using var client = own.CreateClient();

        // Down before the first success: zeros, with the live ingestion fields.
        var first = await StatsAsync(client);
        first.GetProperty("wsStatus").GetString().ShouldBe("Connected");
        first.GetProperty("storedTicksApprox").GetInt64().ShouldBe(0);
        first.GetProperty("activeSymbols").GetInt32().ShouldBe(0);

        counts.Behaviour = _ => Task.FromResult(new StoredCounts(1234, 3));
        own.Clock.Advance(ttl);
        var up = await StatsAsync(client);
        up.GetProperty("storedTicksApprox").GetInt64().ShouldBe(1234);
        up.GetProperty("activeSymbols").GetInt32().ShouldBe(3);

        // Down again: the last-known counts, and the ingestion state as it is now.
        counts.Behaviour = _ => throw new InvalidOperationException("The database is unreachable.");
        state.SetDisconnected();
        own.Clock.Advance(ttl);
        var down = await StatsAsync(client);
        down.GetProperty("wsStatus").GetString().ShouldBe("Disconnected");
        down.GetProperty("connectedSince").ValueKind.ShouldBe(JsonValueKind.Null);
        down.GetProperty("storedTicksApprox").GetInt64().ShouldBe(1234);
        down.GetProperty("activeSymbols").GetInt32().ShouldBe(3);
        counts.Calls.ShouldBe(3);
    }

    [Theory]
    [InlineData("1.0.0+3f2a1b9c0ffee0123456789abcdef01234567", "1.0.0+3f2a1b9")]
    [InlineData("1.2.3-beta.1+3f2a1b9c0ffee", "1.2.3-beta.1+3f2a1b9")]
    [InlineData("1.0.0+abc", "1.0.0+abc")]
    [InlineData("1.0.0", "1.0.0")]
    public void ShortVersion_KeepsSevenCharactersOfTheCommit(string informational, string expected) =>
        SystemStatsProvider.ShortVersion(informational).ShouldBe(expected);

    // ---- /hubs/market --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Hub_Subscribe_ValidatesAndCapsGroups()
    {
        await using var hub = await ConnectAsync(api.Server);

        (await hub.InvokeAsync<bool>("Subscribe", "BTC-USDT", Ct)).ShouldBeFalse();
        (await hub.InvokeAsync<bool>("Subscribe", "", Ct)).ShouldBeFalse();
        (await hub.InvokeAsync<bool>("Subscribe", " btcusdt ", Ct)).ShouldBeTrue();
        (await hub.InvokeAsync<bool>("Subscribe", "BTCUSDT", Ct)).ShouldBeTrue(); // already joined: no second slot

        for (var i = 1; i < MarketHub.MaxSubscriptionsPerConnection; i++)
        {
            (await hub.InvokeAsync<bool>("Subscribe", $"SYM{i:D3}USDT", Ct)).ShouldBeTrue();
        }

        (await hub.InvokeAsync<bool>("Subscribe", "ONEMOREUSDT", Ct)).ShouldBeFalse();
        await hub.InvokeAsync("Unsubscribe", "btcusdt", Ct);
        (await hub.InvokeAsync<bool>("Subscribe", "ONEMOREUSDT", Ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task Hub_OversizedMessage_ClosesTheConnection()
    {
        await using var hub = await ConnectAsync(api.Server);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.Closed += _ =>
        {
            closed.TrySetResult();
            return Task.CompletedTask;
        };

        var error = await Record.ExceptionAsync(() => hub.InvokeAsync<bool>("Subscribe", new string('A', 2 * MarketHubRegistration.MaxReceiveMessageSize), Ct));

        error.ShouldNotBeNull();
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        hub.State.ShouldBe(HubConnectionState.Disconnected);
    }

    [Fact]
    public async Task Hub_ConnectAttempts_AreRateLimitedPerClient()
    {
        await using var own = new ApiFactory(api.Database).WithSetting("MarketHub:ConnectPermitLimit", "2");
        await own.InitializeAsync();
        await using var hub = await ConnectAsync(own.Server); // negotiate: 1 of 2
        using var client = own.CreateClient();

        // Traffic of an established connection (long-polling polls and sends) is not counted.
        for (var i = 0; i < 5; i++)
        {
            (await hub.InvokeAsync<bool>("Subscribe", "BTCUSDT", Ct)).ShouldBeTrue();
        }

        using var second = await client.PostAsync(new Uri("/hubs/market/negotiate?negotiateVersion=1", UriKind.Relative), null, Ct);
        using var third = await client.PostAsync(new Uri("/hubs/market/negotiate?negotiateVersion=1", UriKind.Relative), null, Ct);

        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        third.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        third.Headers.RetryAfter.ShouldNotBeNull();
        (await ReadAsync(third)).Type.ShouldBe(TypeBase + "rate-limited");
        (await hub.InvokeAsync<bool>("Subscribe", "ETHUSDT", Ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task Hub_CountsOpenConnections()
    {
        await using var own = new ApiFactory(api.Database);
        await own.InitializeAsync();
        var connections = own.Services.GetRequiredService<MarketHubConnections>();
        connections.Count.ShouldBe(0);

        var hub = await ConnectAsync(own.Server);
        await IngestionTestHost.WaitForAsync(() => connections.Count == 1, "the hub to count the connection");
        await hub.DisposeAsync();

        await IngestionTestHost.WaitForAsync(() => connections.Count == 0, "the hub to count the disconnect");
    }

    [Fact]
    public async Task Hub_SubscribedClient_ReceivesPrice()
    {
        await using var live = new ApiFactory(api.Database).WithWorkers().WithSystemClock();
        await live.InitializeAsync();
        var state = live.Services.GetRequiredService<IngestionState>();
        await IngestionTestHost.WaitForAsync(() => state.Status == WsState.Connected, "the ingestion worker to connect");
        await live.Binance.WaitUntilAsync(
            s => s.SubscribedStreams.IsSupersetOf(["btcusdt@aggTrade", "ethusdt@aggTrade"]), cancellationToken: Ct);

        var btcPrices = Channel.CreateUnbounded<JsonElement>();
        var ethPrices = Channel.CreateUnbounded<JsonElement>();
        var stats = Channel.CreateUnbounded<JsonElement>();
        await using var btc = await ConnectAsync(live.Server, btcPrices, stats);
        await using var eth = await ConnectAsync(live.Server, ethPrices);
        (await btc.InvokeAsync<bool>("Subscribe", "btcusdt", Ct)).ShouldBeTrue();
        (await eth.InvokeAsync<bool>("Subscribe", "ETHUSDT", Ct)).ShouldBeTrue();

        var tradeTime = IngestionTestHost.Now;
        await live.Binance.PushTradeAsync("BTCUSDT", 900_001, 64_000.12345678m, tradeTime);
        await live.Binance.PushTradeAsync("ETHUSDT", 900_002, 3_000.5m, tradeTime);

        var price = await NextAsync(btcPrices, TimeSpan.FromSeconds(3));
        price.EnumerateObject().Select(p => p.Name).ShouldBe(["symbol", "price", "tradeTime"]);
        price.GetProperty("symbol").GetString().ShouldBe("BTCUSDT");
        price.GetProperty("price").ValueKind.ShouldBe(JsonValueKind.Number);
        price.GetProperty("price").GetDecimal().ShouldBe(64_000.12345678m);
        price.GetProperty("tradeTime").GetString().ShouldBe(Iso(tradeTime));
        (await NextAsync(ethPrices, TimeSpan.FromSeconds(3))).GetProperty("symbol").GetString().ShouldBe("ETHUSDT");

        // A later ETH price reaches the ETH client in a later flush, after anything an earlier flush sent it, so a BTC
        // price leaking into its group would already have arrived.
        await live.Binance.PushTradeAsync("ETHUSDT", 900_003, 3_001m, tradeTime.AddMilliseconds(1));
        (await NextAsync(ethPrices, TimeSpan.FromSeconds(3))).GetProperty("price").GetDecimal().ShouldBe(3_001m);
        ethPrices.Reader.TryRead(out var extra).ShouldBeFalse(extra.ToString());
        btcPrices.Reader.TryRead(out extra).ShouldBeFalse(extra.ToString());

        // Stats go to every connection, every 2 s.
        var pushed = await NextAsync(stats, TimeSpan.FromSeconds(5));
        pushed.EnumerateObject().Select(p => p.Name).ShouldBe(StatsNames);
        pushed.GetProperty("wsStatus").GetString().ShouldBe("Connected");
        pushed.GetProperty("isLeader").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Hub_StalledStatsRead_DoesNotDelayPrices()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var counts = new ControllableCounts
        {
            Behaviour = async ct =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
                throw new InvalidOperationException("The database is unreachable.");
            },
        };

        // A long read timeout, so the read stays stuck until the test lets go.
        await using var own = new ApiFactory(api.Database)
            .WithSystemClock()
            .WithSetting("SystemStats:ReadTimeout", "00:01:00")
            .WithServices(s => s.Replace(ServiceDescriptor.Singleton<IStoredCounts>(counts)));
        await own.InitializeAsync();
        var state = own.Services.GetRequiredService<IngestionState>();
        var prices = Channel.CreateUnbounded<JsonElement>();
        var stats = Channel.CreateUnbounded<JsonElement>();
        await using var hub = await ConnectAsync(own.Server, prices, stats);
        (await hub.InvokeAsync<bool>("Subscribe", "BTCUSDT", Ct)).ShouldBeTrue();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        state.RecordBatch(1, 0, [new LivePrice("BTCUSDT", 42.5m, IngestionTestHost.Now)]);

        // Within four flush intervals, while the statistics read is still stuck.
        var price = await NextAsync(prices, TimeSpan.FromSeconds(1));
        price.GetProperty("price").GetDecimal().ShouldBe(42.5m);
        release.Task.IsCompleted.ShouldBeFalse();
        stats.Reader.TryRead(out _).ShouldBeFalse();

        // The read then fails: the push still goes out, with zero counts (nothing was read yet) and the live state.
        release.TrySetResult();
        var pushed = await NextAsync(stats, TimeSpan.FromSeconds(3));
        pushed.GetProperty("wsStatus").GetString().ShouldBe("Standby");
        pushed.GetProperty("storedTicksApprox").GetInt64().ShouldBe(0);
        pushed.GetProperty("activeSymbols").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task Hub_OlderPrice_IsNeverSent()
    {
        await using var own = new ApiFactory(api.Database).WithSystemClock();
        await own.InitializeAsync();
        var state = own.Services.GetRequiredService<IngestionState>();
        var prices = Channel.CreateUnbounded<JsonElement>();
        await using var hub = await ConnectAsync(own.Server, prices);
        (await hub.InvokeAsync<bool>("Subscribe", "BTCUSDT", Ct)).ShouldBeTrue();
        (await hub.InvokeAsync<bool>("Subscribe", "ETHUSDT", Ct)).ShouldBeTrue();
        var t = IngestionTestHost.Now;

        state.RecordBatch(0, 0, [new LivePrice("BTCUSDT", 100m, t)]);
        (await NextAsync(prices, TimeSpan.FromSeconds(3))).GetProperty("price").GetDecimal().ShouldBe(100m);

        // Forgotten (e.g. removed and added again), so the state accepts an older trade; the hub must not go back.
        state.ForgetSymbol("BTCUSDT");
        state.RecordBatch(0, 0, [new LivePrice("BTCUSDT", 99m, t.AddSeconds(-1))]);
        state.RecordBatch(0, 0, [new LivePrice("ETHUSDT", 1m, t)]);
        var seen = await ReadUntilAsync(prices, p => p.GetProperty("price").GetDecimal() == 1m);

        // A second ETH price goes out in a later flush, so anything sent with the older BTC price has arrived by then.
        state.RecordBatch(0, 0, [new LivePrice("ETHUSDT", 2m, t.AddMilliseconds(1))]);
        seen.AddRange(await ReadUntilAsync(prices, p => p.GetProperty("price").GetDecimal() == 2m));
        seen.ShouldAllBe(p => p.GetProperty("symbol").GetString() == "ETHUSDT");

        state.RecordBatch(0, 0, [new LivePrice("BTCUSDT", 101m, t.AddSeconds(1))]);
        (await NextAsync(prices, TimeSpan.FromSeconds(3))).GetProperty("price").GetDecimal().ShouldBe(101m);
    }

    [Fact]
    public async Task Broadcaster_AfterStop_QueuesNothing()
    {
        await using var own = new ApiFactory(api.Database);
        await own.InitializeAsync();
        _ = own.Server;
        var state = own.Services.GetRequiredService<IngestionState>();
        var broadcaster = own.Services.GetServices<IHostedService>().OfType<MarketBroadcaster>().Single();
        var t = own.Clock.GetUtcNow().UtcDateTime;

        // No hub client and a frozen clock: a queued price stays queued.
        state.RecordBatch(0, 0, [new LivePrice("BTCUSDT", 1m, t)]);
        broadcaster.PendingCount.ShouldBe(1);

        await broadcaster.StopAsync(Ct);
        state.RecordBatch(0, 0, [new LivePrice("ETHUSDT", 2m, t)]);

        broadcaster.PendingCount.ShouldBe(1);
    }

    // ---- helpers -------------------------------------------------------------------------------------------------

    private static string Iso(DateTime utc) => utc.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture);

    private static string InformationalVersion() =>
        typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;

    private static async Task<JsonElement> StatsAsync(HttpClient client)
    {
        using var response = await client.SendAsync(Get("/api/system/stats", accept: null), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return json.RootElement.Clone();
    }

    private static async Task<long> UptimeAsync(HttpClient client) => (await StatsAsync(client)).GetProperty("uptimeSeconds").GetInt64();

    private static async Task<HubConnection> ConnectAsync(
        TestServer server,
        Channel<JsonElement>? prices = null,
        Channel<JsonElement>? stats = null)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, "hubs/market"), o =>
            {
                o.Transports = HttpTransportType.LongPolling;
                o.HttpMessageHandlerFactory = _ => server.CreateHandler();
            })
            .Build();
        if (prices is not null)
        {
            connection.On<JsonElement>("price", e => prices.Writer.TryWrite(e.Clone()));
        }

        if (stats is not null)
        {
            connection.On<JsonElement>("stats", e => stats.Writer.TryWrite(e.Clone()));
        }

        await connection.StartAsync(Ct);
        return connection;
    }

    private static async Task<JsonElement> NextAsync(Channel<JsonElement> channel, TimeSpan timeout)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(timeout);
        try
        {
            return await channel.Reader.ReadAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!Ct.IsCancellationRequested)
        {
            throw new TimeoutException($"No hub message within {timeout.TotalSeconds} s.");
        }
    }

    /// <summary>Reads messages up to and including the first that matches.</summary>
    private static async Task<List<JsonElement>> ReadUntilAsync(Channel<JsonElement> channel, Func<JsonElement, bool> last)
    {
        List<JsonElement> read = [];
        while (true)
        {
            var message = await NextAsync(channel, TimeSpan.FromSeconds(3));
            read.Add(message);
            if (last(message))
            {
                return read;
            }
        }
    }

    /// <summary>The stored counts, as the test wants them: values, a failure, or a read that hangs.</summary>
    private sealed class ControllableCounts : IStoredCounts
    {
        private int _calls;

        public Func<CancellationToken, Task<StoredCounts>> Behaviour { get; set; } = _ => Task.FromResult(new StoredCounts(0, 0));

        public int Calls => Volatile.Read(ref _calls);

        public Task<StoredCounts> GetAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return Behaviour(ct);
        }
    }
}
