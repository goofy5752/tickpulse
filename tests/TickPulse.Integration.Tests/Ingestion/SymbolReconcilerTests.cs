using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TickPulse.Domain;
using TickPulse.Infrastructure.Ingestion;
using TickPulse.Integration.Tests.Fixtures;
using static TickPulse.Integration.Tests.Ingestion.IngestionTestHost;

namespace TickPulse.Integration.Tests.Ingestion;

/// <summary>
/// The reconciler never assumes a command succeeded: these tests lose or reject acks and check that the
/// subscriptions still converge. Commands time out after 300 ms instead of 10 s.
/// </summary>
public sealed class SymbolReconcilerTests(TimescaleFixture db) : IAsyncLifetime
{
    private static readonly string[] Seeded = ["BTCUSDT", "ADAUSDT", "ETHUSDT"];
    private static readonly TimeSpan AckTimeout = TimeSpan.FromMilliseconds(300);

    private readonly FakeBinanceServer _server = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await db.ResetAsync();
        await _server.InitializeAsync();
        _server.MaxIncomingMessagesPerSecond = 5;
    }

    public async ValueTask DisposeAsync()
    {
        var rateLimitDisconnects = _server.RateLimitDisconnects;
        await _server.DisposeAsync();
        rateLimitDisconnects.ShouldBe(0);
    }

    [Fact]
    public async Task SubscribeAckLost_StateUnknown_ResentAndStreamed()
    {
        await using var host = await StartHostAsync();

        _server.SuppressNextAck();
        await host.Symbols.AddOrReactivateAsync(Symbol("XRPUSDT"), 20, Ct);
        host.Signal.Notify();

        // The server applied the first SUBSCRIBE but never acked it; the retry pass sends it again.
        await _server.WaitUntilAsync(s => Commands("SUBSCRIBE", "XRPUSDT").Length == 2, WaitTimeout, Ct);
        await WaitForAsync(() => host.Logs.Count("LogSubscribed") == 2, "the resent SUBSCRIBE acked");
        host.Logs.Count("LogTimedOut").ShouldBe(1);
        Commands("UNSUBSCRIBE", "XRPUSDT").ShouldBeEmpty();

        await _server.PushTradeAsync("XRPUSDT", 1, 0.5m, Now.AddSeconds(-1));
        await WaitForTradeCountAsync(db, "XRPUSDT", 1);
        _server.AcceptedConnections.ShouldBe(1);
    }

    [Fact]
    public async Task UnsubscribeAckLost_ThenReadded_SubscribedAgain()
    {
        await using var host = await StartHostAsync();
        var eth = Symbol("ETHUSDT");

        _server.SuppressNextAck();
        await host.Symbols.DeactivateAsync(eth, Ct);
        host.Signal.Notify();
        await _server.WaitUntilAsync(s => !s.SubscribedStreams.Contains(Stream("ETHUSDT")), WaitTimeout, Ct);

        // Binance applied the UNSUBSCRIBE, but the worker cannot know that. When the symbol comes back, it must not
        // assume "still subscribed" and stay silent.
        await host.Symbols.AddOrReactivateAsync(eth, 20, Ct);
        host.Signal.Notify();

        await _server.WaitUntilAsync(s => s.SubscribedStreams.SetEquals(Seeded.Select(Stream)), WaitTimeout, Ct);
        await WaitForAsync(() => host.Logs.Count("LogTimedOut") == 1, "the timeout logged");
        await _server.PushTradeAsync("ETHUSDT", 1, 3000m, Now.AddSeconds(-1));
        await WaitForTradeCountAsync(db, "ETHUSDT", 1);
    }

    [Fact]
    public async Task SubscribeRejected_RetriedOnTheNextPass()
    {
        await using var host = await StartHostAsync(reconcileInterval: "00:00:01");

        _server.RejectNextCommand(-1, "Internal error");
        await host.Symbols.AddOrReactivateAsync(Symbol("XRPUSDT"), 20, Ct);
        host.Signal.Notify();

        await _server.WaitUntilAsync(s => s.SubscribedStreams.Contains(Stream("XRPUSDT")), WaitTimeout, Ct);
        Commands("SUBSCRIBE", "XRPUSDT").Length.ShouldBe(2);
        host.Logs.Count("LogRejected").ShouldBe(1);
        host.Logs.Entries.ShouldContain(e => e.EventName == "LogRejected" && e.Level == LogLevel.Warning);
    }

    private static SymbolName Symbol(string raw) => SymbolName.TryCreate(raw, out var name) ? name : throw new ArgumentException(raw);

    private async Task<IngestionTestHost> StartHostAsync(string reconcileInterval = "00:00:30")
    {
        var host = await StartAsync(
            db,
            _server,
            new Dictionary<string, string?> { ["Ingestion:ReconcileInterval"] = reconcileInterval },
            services => services.AddSingleton<IStreamConnectionFactory>(sp => new StreamConnectionFactory(
                _server.WsBaseUrl, TimeProvider.System, sp.GetRequiredService<ILoggerFactory>(), AckTimeout)));
        await _server.WaitUntilAsync(s => s.ActiveConnections == 1 && s.SubscribedStreams.SetEquals(Seeded.Select(Stream)), WaitTimeout, Ct);
        return host;
    }

    // Commands of the given method whose params include the symbol's stream.
    private string[] Commands(string method, string symbol) =>
        [.. _server.ReceivedCommands.Where(json =>
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return root.GetProperty("method").GetString() == method
                && root.GetProperty("params").EnumerateArray().Any(p => p.GetString() == Stream(symbol));
        })];
}
