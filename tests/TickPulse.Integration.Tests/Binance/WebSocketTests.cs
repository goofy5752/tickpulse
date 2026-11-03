using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using TickPulse.Infrastructure.Binance;
using TickPulse.Integration.Tests.Fixtures;

namespace TickPulse.Integration.Tests.Binance;

public sealed class WebSocketTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly DateTime TradeTime = new(2026, 11, 3, 11, 59, 59, 250, DateTimeKind.Utc);

    private readonly FakeBinanceServer _server = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero));

    public ValueTask InitializeAsync() => _server.InitializeAsync();

    public ValueTask DisposeAsync() => _server.DisposeAsync();

    [Fact]
    public async Task Connect_SubscribeAndReceiveTrade()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new BinanceWebSocketConnection(_server.WsBaseUrl, _clock);

        await connection.ConnectAsync(ct);
        await connection.SubscribeAsync(["BTCUSDT"], ct);
        await _server.PushTradeAsync("BTCUSDT", 42, 67012.34m, TradeTime);
        var trade = await connection.Trades.ReadAsync(ct).AsTask().WaitAsync(Timeout, ct);

        connection.ConnectedAtUtc.ShouldBe(new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc));
        connection.ConnectedAtUtc.Kind.ShouldBe(DateTimeKind.Utc);
        _server.ActiveConnections.ShouldBe(1);
        trade.ShouldBe(new AggTradeEvent("BTCUSDT", 42, 67012.34m, 1m, TradeTime, TradeTime));
        var command = ParseCommand(_server.ReceivedCommands.ShouldHaveSingleItem());
        command.Method.ShouldBe("SUBSCRIBE");
        command.Params.ShouldBe(["btcusdt@aggTrade"]);
        command.Id.ShouldBe(1);
    }

    [Fact]
    public async Task Unsubscribe_StopsTrades()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new BinanceWebSocketConnection(_server.WsBaseUrl, _clock);
        await connection.ConnectAsync(ct);
        await connection.SubscribeAsync(["BTCUSDT", "ethusdt"], ct);
        await _server.PushTradeAsync("BTCUSDT", 1, 100m, TradeTime);
        (await connection.Trades.ReadAsync(ct).AsTask().WaitAsync(Timeout, ct)).AggTradeId.ShouldBe(1);

        await connection.UnsubscribeAsync(["BTCUSDT"], ct);
        await _server.PushTradeAsync("BTCUSDT", 2, 101m, TradeTime);
        await _server.PushTradeAsync("ETHUSDT", 3, 3000m, TradeTime);

        // One socket keeps order: if trade 2 had been delivered it would arrive before trade 3.
        var next = await connection.Trades.ReadAsync(ct).AsTask().WaitAsync(Timeout, ct);
        next.Symbol.ShouldBe("ETHUSDT");
        next.AggTradeId.ShouldBe(3);
        connection.Trades.TryRead(out _).ShouldBeFalse();

        var commands = _server.ReceivedCommands.Select(ParseCommand).ToArray();
        commands.Length.ShouldBe(2);
        commands[0].Method.ShouldBe("SUBSCRIBE");
        commands[0].Params.ShouldBe(["btcusdt@aggTrade", "ethusdt@aggTrade"]);
        commands[0].Id.ShouldBe(1);
        commands[1].Method.ShouldBe("UNSUBSCRIBE");
        commands[1].Params.ShouldBe(["btcusdt@aggTrade"]);
        commands[1].Id.ShouldBe(2);
    }

    [Fact]
    public async Task ServerDrop_CompletesConnection()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new BinanceWebSocketConnection(_server.WsBaseUrl, _clock);
        await connection.ConnectAsync(ct);
        await _server.WaitUntilAsync(s => s.ActiveConnections == 1, Timeout, ct);

        await _server.DropAllConnectionsAsync();

        await Should.ThrowAsync<WebSocketException>(() => connection.Completion.WaitAsync(Timeout, ct));
        connection.Completion.IsFaulted.ShouldBeTrue();
        await connection.Trades.Completion.WaitAsync(Timeout, ct);
        _server.ActiveConnections.ShouldBe(0);
    }

    [Fact]
    public async Task ServerClose_CompletesConnectionWithoutFault()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new BinanceWebSocketConnection(_server.WsBaseUrl, _clock);
        await connection.ConnectAsync(ct);
        await _server.WaitUntilAsync(s => s.ActiveConnections == 1, Timeout, ct);

        await _server.CloseAllConnectionsAsync();

        await connection.Completion.WaitAsync(Timeout, ct);
        connection.Completion.IsCompletedSuccessfully.ShouldBeTrue();
        await connection.Trades.Completion.WaitAsync(Timeout, ct);
    }

    [Fact]
    public async Task Subscribe_ServerRejects_ThrowsAndConnectionStaysUsable()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new BinanceWebSocketConnection(_server.WsBaseUrl, _clock);
        await connection.ConnectAsync(ct);

        _server.RejectNextCommand(2, "Invalid request");
        var error = await Should.ThrowAsync<BinanceCommandException>(() => connection.SubscribeAsync(["BTCUSDT"], ct));
        await connection.SubscribeAsync(["ETHUSDT"], ct);
        await _server.PushTradeAsync("ETHUSDT", 5, 3000m, TradeTime);

        error.Code.ShouldBe(2);
        error.CommandId.ShouldBe(1);
        (await connection.Trades.ReadAsync(ct).AsTask().WaitAsync(Timeout, ct)).AggTradeId.ShouldBe(5);
    }

    [Fact]
    public async Task MalformedMessage_IsSkipped()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new BinanceWebSocketConnection(_server.WsBaseUrl, _clock);
        await connection.ConnectAsync(ct);
        await connection.SubscribeAsync(["BTCUSDT"], ct);

        await _server.PushRawAsync("""{"stream":"btcusdt@aggTrade","data":{"e":"aggTrade","p":"oops"}}""");
        await _server.PushRawAsync(new string('x', 200_000));
        await _server.PushRawAsync(new string('y', 1_500_000));
        await _server.PushTradeAsync("BTCUSDT", 7, 100m, TradeTime);

        (await connection.Trades.ReadAsync(ct).AsTask().WaitAsync(Timeout, ct)).AggTradeId.ShouldBe(7);
        connection.Completion.IsCompleted.ShouldBeFalse();
    }

    [Fact]
    public async Task Subscribe_FiveCommandsInOneSecond_FifthWaitsForThrottle()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new BinanceWebSocketConnection(_server.WsBaseUrl, _clock);
        await connection.ConnectAsync(ct);
        string[] symbols = ["BTCUSDT", "ETHUSDT", "ADAUSDT", "BNBUSDT"];
        foreach (var symbol in symbols)
        {
            await connection.SubscribeAsync([symbol], ct);
        }

        var fifth = connection.SubscribeAsync(["XRPUSDT"], ct);
        await _server.WaitUntilAsync(s => s.ReceivedCommands.Count == 4, Timeout, ct);
        fifth.IsCompleted.ShouldBeFalse();

        _clock.Advance(TimeSpan.FromSeconds(1));
        await fifth.WaitAsync(Timeout, ct);

        var ids = _server.ReceivedCommands.Select(c => ParseCommand(c).Id).ToArray();
        ids.ShouldBe([1L, 2L, 3L, 4L, 5L]);
        _server.SubscribedStreams.ShouldContain("xrpusdt@aggTrade");
    }

    [Fact]
    public async Task Subscribe_BeforeConnect_Throws()
    {
        await using var connection = new BinanceWebSocketConnection(_server.WsBaseUrl, _clock);

        await Should.ThrowAsync<InvalidOperationException>(
            () => connection.SubscribeAsync(["BTCUSDT"], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Dispose_ClosesServerSide()
    {
        var ct = TestContext.Current.CancellationToken;
        var connection = new BinanceWebSocketConnection(_server.WsBaseUrl, _clock);
        await connection.ConnectAsync(ct);
        await _server.WaitUntilAsync(s => s.ActiveConnections == 1, Timeout, ct);

        await connection.DisposeAsync();

        await _server.WaitUntilAsync(s => s.ActiveConnections == 0, Timeout, ct);
        connection.Completion.IsCompletedSuccessfully.ShouldBeTrue();
    }

    private static Command ParseCommand(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        return new Command(
            root.GetProperty("method").GetString()!,
            [.. root.GetProperty("params").EnumerateArray().Select(p => p.GetString()!)],
            root.GetProperty("id").GetInt64());
    }

    private sealed record Command(string Method, string[] Params, long Id);
}
