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
using Microsoft.Extensions.Hosting;
using TickPulse.Api.Hubs;
using TickPulse.Application.Abstractions;
using TickPulse.Application.Ingestion;
using TickPulse.Integration.Tests.Fixtures;
using TickPulse.Integration.Tests.Ingestion;
using static TickPulse.Integration.Tests.Api.ProblemReader;

namespace TickPulse.Integration.Tests.Api;

/// <summary>
/// <c>GET /api/system/stats</c> against a hand-set <see cref="IngestionState"/> (fake clock, no workers), and the
/// market hub end to end: a trade pushed by the fake Binance server goes through the real ingestion worker and the
/// broadcaster to a <see cref="HubConnection"/> on the test server.
/// </summary>
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
        var now = api.Clock.GetUtcNow().UtcDateTime;
        var state = api.Services.GetRequiredService<IngestionState>();
        state.SetConnected(now.AddMinutes(-10), now.AddHours(22));
        state.MarkReconnected(now.AddMinutes(-10));

        // The clock is frozen, so the rate does not decay: 50 ticks over the 10 s window are 5 ticks/s.
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
        root.GetProperty("version").GetString().ShouldBe(ExpectedVersion());
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
        using var client = api.CreateClient();
        var before = await UptimeAsync(client);

        api.Clock.Advance(TimeSpan.FromSeconds(90));
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

        var price = await ReadAsync(btcPrices, TimeSpan.FromSeconds(3));
        price.EnumerateObject().Select(p => p.Name).ShouldBe(["symbol", "price", "tradeTime"]);
        price.GetProperty("symbol").GetString().ShouldBe("BTCUSDT");
        price.GetProperty("price").ValueKind.ShouldBe(JsonValueKind.Number);
        price.GetProperty("price").GetDecimal().ShouldBe(64_000.12345678m);
        price.GetProperty("tradeTime").GetString().ShouldBe(Iso(tradeTime));
        (await ReadAsync(ethPrices, TimeSpan.FromSeconds(3))).GetProperty("symbol").GetString().ShouldBe("ETHUSDT");

        // A later ETH price reaches the ETH client in a later flush, after anything an earlier flush sent it, so a BTC
        // price leaking into its group would already have arrived.
        await live.Binance.PushTradeAsync("ETHUSDT", 900_003, 3_001m, tradeTime.AddMilliseconds(1));
        (await ReadAsync(ethPrices, TimeSpan.FromSeconds(3))).GetProperty("price").GetDecimal().ShouldBe(3_001m);
        ethPrices.Reader.TryRead(out var extra).ShouldBeFalse(extra.ToString());
        btcPrices.Reader.TryRead(out extra).ShouldBeFalse(extra.ToString());

        // Stats go to every connection, every 2 s.
        var pushed = await ReadAsync(stats, TimeSpan.FromSeconds(5));
        pushed.EnumerateObject().Select(p => p.Name).ShouldBe(StatsNames);
        pushed.GetProperty("wsStatus").GetString().ShouldBe("Connected");
        pushed.GetProperty("isLeader").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Broadcaster_OnStop_UnsubscribesFromPriceUpdates()
    {
        await using var own = new ApiFactory(api.Database);
        await own.InitializeAsync();
        _ = own.Server;
        var state = own.Services.GetRequiredService<IngestionState>();
        var broadcaster = own.Services.GetServices<IHostedService>().OfType<MarketBroadcaster>().Single();
        PriceHandlerTargets(state).ShouldContain(broadcaster);

        await broadcaster.StopAsync(Ct);

        PriceHandlerTargets(state).ShouldNotContain(broadcaster);
    }

    // ---- helpers -------------------------------------------------------------------------------------------------

    private static string Iso(DateTime utc) => utc.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture);

    private static string ExpectedVersion() =>
        typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;

    private static async Task<long> UptimeAsync(HttpClient client)
    {
        using var response = await client.SendAsync(Get("/api/system/stats", accept: null), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return json.RootElement.GetProperty("uptimeSeconds").GetInt64();
    }

    private static object?[] PriceHandlerTargets(IngestionState state)
    {
        var field = typeof(IngestionState).GetField(nameof(IngestionState.PriceUpdated), BindingFlags.Instance | BindingFlags.NonPublic)!;
        var handlers = (Delegate?)field.GetValue(state);
        return handlers?.GetInvocationList().Select(d => d.Target).ToArray() ?? [];
    }

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

    private static async Task<JsonElement> ReadAsync(Channel<JsonElement> channel, TimeSpan timeout)
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
}
