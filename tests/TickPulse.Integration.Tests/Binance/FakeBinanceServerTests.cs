using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TickPulse.Integration.Tests.Fixtures;

namespace TickPulse.Integration.Tests.Binance;

public sealed class FakeBinanceServerTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly DateTime Minute0 = new(2026, 11, 3, 10, 0, 0, DateTimeKind.Utc);

    private readonly FakeBinanceServer _server = new();

    public ValueTask InitializeAsync() => _server.InitializeAsync();

    public ValueTask DisposeAsync() => _server.DisposeAsync();

    [Fact]
    public async Task RateLimit_SixMessagesInOneSecond_ClosesConnection()
    {
        var ct = TestContext.Current.CancellationToken;
        _server.MaxIncomingMessagesPerSecond = 5;
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(_server.WsBaseUrl, "/stream"), ct);

        for (var id = 1; id <= 6; id++)
        {
            var command = Encoding.UTF8.GetBytes($$"""{"method":"LIST_SUBSCRIPTIONS","id":{{id}}}""");
            await socket.SendAsync(command, WebSocketMessageType.Text, endOfMessage: true, ct);
        }

        var buffer = new byte[4096];
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, ct).WaitAsync(Timeout, ct);
        }
        while (result.MessageType != WebSocketMessageType.Close);

        socket.CloseStatus.ShouldBe(WebSocketCloseStatus.PolicyViolation);
        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, ct);
        await _server.WaitUntilAsync(s => s.ActiveConnections == 0, Timeout, ct);
        _server.RateLimitDisconnects.ShouldBe(1);
        _server.ReceivedCommands.Count.ShouldBe(5);
    }

    [Fact]
    public async Task Klines_StartTime_ReturnsAscendingFromStartUpToLimit()
    {
        AddOneTradePerMinute(5);

        var openTimes = await KlineOpenTimesAsync($"startTime={Ms(Minute0.AddMinutes(1))}&limit=2");

        openTimes.ShouldBe([Ms(Minute0.AddMinutes(1)), Ms(Minute0.AddMinutes(2))]);
    }

    [Fact]
    public async Task Klines_EndTimeOnly_ReturnsLatestLimitEndingAtEndTime()
    {
        AddOneTradePerMinute(5);

        var openTimes = await KlineOpenTimesAsync($"endTime={Ms(Minute0.AddMinutes(3))}&limit=2");

        openTimes.ShouldBe([Ms(Minute0.AddMinutes(2)), Ms(Minute0.AddMinutes(3))]);
    }

    [Fact]
    public async Task Klines_NoTimes_ReturnsLatestLimit()
    {
        AddOneTradePerMinute(5);

        var openTimes = await KlineOpenTimesAsync("limit=2");

        openTimes.ShouldBe([Ms(Minute0.AddMinutes(3)), Ms(Minute0.AddMinutes(4))]);
    }

    [Fact]
    public async Task Klines_MinuteWithoutTrades_HasNoKline()
    {
        _server.AddTrade("BTCUSDT", 1, 100m, Minute0);
        _server.AddTrade("BTCUSDT", 2, 101m, Minute0.AddMinutes(2));

        var openTimes = await KlineOpenTimesAsync($"startTime={Ms(Minute0)}");

        openTimes.ShouldBe([Ms(Minute0), Ms(Minute0.AddMinutes(2))]);
    }

    private static long Ms(DateTime utc) => new DateTimeOffset(utc).ToUnixTimeMilliseconds();

    private void AddOneTradePerMinute(int minutes)
    {
        for (var i = 0; i < minutes; i++)
        {
            _server.AddTrade("BTCUSDT", i + 1, 100m + i, Minute0.AddMinutes(i).AddSeconds(30));
        }
    }

    private async Task<long[]> KlineOpenTimesAsync(string query)
    {
        using var http = new HttpClient { BaseAddress = _server.RestBaseUrl };
        var json = await http.GetStringAsync(
            new Uri($"/api/v3/klines?symbol=BTCUSDT&interval=1m&{query}", UriKind.Relative),
            TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(json);
        return [.. document.RootElement.EnumerateArray().Select(row => row[0].GetInt64())];
    }
}
