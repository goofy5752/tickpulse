using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;
using TickPulse.Application.Abstractions;
using TickPulse.Domain;
using TickPulse.Infrastructure.Binance;
using TickPulse.Integration.Tests.Fixtures;

namespace TickPulse.Integration.Tests.Binance;

public sealed class BinanceRestClientTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly Uri BaseUrl = new("https://binance.test/");
    private static readonly SymbolName Btc = Symbol("BTCUSDT");
    private static readonly DateTimeOffset Noon = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    private const string TradingInfo = """{"timezone":"UTC","symbols":[{"symbol":"BTCUSDT","status":"TRADING"}]}""";
    private const string TooManyRequests = """{"code":-1003,"msg":"Too many requests."}""";

    [Fact]
    public async Task IsTrading_TradingSymbol_True()
    {
        var stub = new StubHandler();
        stub.Enqueue(HttpStatusCode.OK, TradingInfo);
        using var client = NewClient(stub, new FakeTimeProvider(), out var rest);

        (await rest.IsTradingSymbolAsync(Btc, TestContext.Current.CancellationToken)).ShouldBeTrue();

        stub.Requests.ShouldHaveSingleItem().PathAndQuery.ShouldBe("/api/v3/exchangeInfo?symbol=BTCUSDT");
    }

    [Fact]
    public async Task IsTrading_UnknownSymbol_False()
    {
        var stub = new StubHandler();
        stub.Enqueue(HttpStatusCode.BadRequest, """{"code":-1121,"msg":"Invalid symbol."}""");
        using var client = NewClient(stub, new FakeTimeProvider(), out var rest);

        (await rest.IsTradingSymbolAsync(Symbol("NOPEUSDT"), TestContext.Current.CancellationToken)).ShouldBeFalse();
    }

    [Fact]
    public async Task IsTrading_BreakSymbol_False()
    {
        var stub = new StubHandler();
        stub.Enqueue(HttpStatusCode.OK, """{"symbols":[{"symbol":"LUNAUSDT","status":"BREAK"}]}""");
        using var client = NewClient(stub, new FakeTimeProvider(), out var rest);

        (await rest.IsTradingSymbolAsync(Symbol("LUNAUSDT"), TestContext.Current.CancellationToken)).ShouldBeFalse();
    }

    [Fact]
    public async Task IsTrading_OtherBadRequest_ThrowsNonTransientApiException()
    {
        var stub = new StubHandler();
        stub.Enqueue(HttpStatusCode.BadRequest, """{"code":-1100,"msg":"Illegal characters found in a parameter."}""");
        using var client = NewClient(stub, new FakeTimeProvider(), out var rest);

        var ex = await Should.ThrowAsync<BinanceApiException>(
            () => rest.IsTradingSymbolAsync(Btc, TestContext.Current.CancellationToken));

        ex.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        ex.ErrorCode.ShouldBe(-1100);
        ex.IsTransient.ShouldBeFalse();
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, TradingInfo, true)]
    [InlineData(HttpStatusCode.BadRequest, """{"code":-1121,"msg":"Invalid symbol."}""", false)]
    public async Task ExchangeMetadata_Answer_PassesThrough(HttpStatusCode status, string body, bool expected)
    {
        var stub = new StubHandler();
        stub.Enqueue(status, body);
        using var client = NewClient(stub, new FakeTimeProvider(), out var rest);

        (await new BinanceExchangeMetadata(rest).IsTradingSymbolAsync(Btc, TestContext.Current.CancellationToken)).ShouldBe(expected);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    public async Task ExchangeMetadata_TransientFailure_ThrowsExchangeUnavailable(HttpStatusCode status)
    {
        var stub = new StubHandler();
        stub.Enqueue(status, "<html>upstream error</html>");
        using var client = NewClient(stub, new FakeTimeProvider(), out var rest);

        var ex = await Should.ThrowAsync<ExchangeUnavailableException>(
            () => new BinanceExchangeMetadata(rest).IsTradingSymbolAsync(Btc, TestContext.Current.CancellationToken));

        ex.InnerException.ShouldBeOfType<BinanceApiException>().IsTransient.ShouldBeTrue();
    }

    [Fact]
    public async Task ExchangeMetadata_Banned_ThrowsExchangeUnavailable()
    {
        var stub = new StubHandler();
        stub.Enqueue((HttpStatusCode)418, "", retryAfterSeconds: 120);
        using var client = NewClient(stub, new FakeTimeProvider(Noon), out var rest);

        var ex = await Should.ThrowAsync<ExchangeUnavailableException>(
            () => new BinanceExchangeMetadata(rest).IsTradingSymbolAsync(Btc, TestContext.Current.CancellationToken));

        ex.InnerException.ShouldBeOfType<BinanceBannedException>();
    }

    [Fact]
    public async Task ExchangeMetadata_PermanentFailure_Propagates()
    {
        var stub = new StubHandler();
        stub.Enqueue(HttpStatusCode.BadRequest, """{"code":-1100,"msg":"Illegal characters found in a parameter."}""");
        using var client = NewClient(stub, new FakeTimeProvider(), out var rest);

        var ex = await Should.ThrowAsync<BinanceApiException>(
            () => new BinanceExchangeMetadata(rest).IsTradingSymbolAsync(Btc, TestContext.Current.CancellationToken));

        ex.IsTransient.ShouldBeFalse();
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    public async Task ErrorStatus_ServerSide_ThrowsTransientApiException(HttpStatusCode status)
    {
        var stub = new StubHandler();
        stub.Enqueue(status, "<html>upstream error</html>");
        using var client = NewClient(stub, new FakeTimeProvider(), out var rest);

        var ex = await Should.ThrowAsync<BinanceApiException>(
            () => rest.GetAggTradesFromAsync(Btc, 1, 10, TestContext.Current.CancellationToken));

        ex.StatusCode.ShouldBe(status);
        ex.ErrorCode.ShouldBeNull();
        ex.IsTransient.ShouldBeTrue();
    }

    [Theory]
    [InlineData("timeout-rejected")]
    [InlineData("broken-circuit")]
    [InlineData("http-client-timeout")]
    [InlineData("network")]
    public async Task NoResponse_ThrowsTransientApiExceptionWith503(string failure)
    {
        Exception thrown = failure switch
        {
            "timeout-rejected" => new TimeoutRejectedException(),
            "broken-circuit" => new BrokenCircuitException(),
            "http-client-timeout" => new TaskCanceledException("The request was canceled.", new TimeoutException()),
            _ => new HttpRequestException("Connection refused"),
        };
        var stub = new StubHandler();
        stub.EnqueueThrow(_ => thrown);
        using var client = NewClient(stub, new FakeTimeProvider(), out var rest);

        var ex = await Should.ThrowAsync<BinanceApiException>(
            () => rest.IsTradingSymbolAsync(Btc, TestContext.Current.CancellationToken));

        ex.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        ex.IsTransient.ShouldBeTrue();
        ex.InnerException.ShouldBeSameAs(thrown);
    }

    [Fact]
    public async Task CallerCancellation_DuringRequest_ThrowsOperationCanceled()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var stub = new StubHandler();
        stub.EnqueueThrow(_ =>
        {
            cts.Cancel();
            return new TaskCanceledException("canceled", null, cts.Token);
        });
        using var client = NewClient(stub, new FakeTimeProvider(), out var rest);

        var ex = await CatchAsync(() => rest.IsTradingSymbolAsync(Btc, cts.Token));

        ex.ShouldBeAssignableTo<OperationCanceledException>();
    }

    [Fact]
    public async Task Klines_ParsesDecimalStringsInvariantCulture()
    {
        var stub = new StubHandler();
        stub.Enqueue(HttpStatusCode.OK, """
            [[1762171200000,"65000.12000000","65010.50000000","64990.01000000","65005.99000000","12.34567800",1762171259999,"802469.12345678",42,"1.00000000","65000.00000000","0"],
             [1762171260000,"0.00001234","0.00001300","0.00001200","0.00001250","1000000.00000000",1762171319999,"12.50000000",7,"0","0","0"]]
            """);
        using var client = NewClient(stub, new FakeTimeProvider(), out var rest);
        var from = new DateTime(2025, 11, 3, 12, 0, 0, DateTimeKind.Utc);
        var to = from.AddMinutes(2);

        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = BulgarianNumberCulture();
        IReadOnlyList<Kline> klines;
        try
        {
            klines = await rest.GetMinuteKlinesAsync(Btc, from, to, 1000, TestContext.Current.CancellationToken);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }

        klines.Count.ShouldBe(2);
        klines[0].ShouldBe(new Kline(from, 65000.12m, 65010.5m, 64990.01m, 65005.99m, 12.345678m, 802469.12345678m));
        klines[0].OpenTimeUtc.Kind.ShouldBe(DateTimeKind.Utc);
        klines[1].ShouldBe(new Kline(from.AddMinutes(1), 0.00001234m, 0.000013m, 0.000012m, 0.0000125m, 1000000m, 12.5m));
        stub.Requests.ShouldHaveSingleItem().PathAndQuery.ShouldBe(
            "/api/v3/klines?symbol=BTCUSDT&interval=1m&startTime=1762171200000&endTime=1762171320000&limit=1000");
    }

    [Fact]
    public async Task Klines_ZeroVolumeMinute_IsReturned()
    {
        var stub = new StubHandler();
        stub.Enqueue(HttpStatusCode.OK, """
            [[1762171200000,"65005.99000000","65005.99000000","65005.99000000","65005.99000000","0.00000000",1762171259999,"0.00000000",0,"0.00000000","0.00000000","0"]]
            """);
        using var client = NewClient(stub, new FakeTimeProvider(), out var rest);
        var from = new DateTime(2025, 11, 3, 12, 0, 0, DateTimeKind.Utc);

        var klines = await rest.GetMinuteKlinesAsync(Btc, from, from, 1, TestContext.Current.CancellationToken);

        klines.ShouldHaveSingleItem().ShouldBe(new Kline(from, 65005.99m, 65005.99m, 65005.99m, 65005.99m, 0m, 0m));
    }

    [Fact]
    public async Task AggTrades_FromId_ParsesTrades()
    {
        var stub = new StubHandler();
        stub.Enqueue(HttpStatusCode.OK, """
            [{"a":101,"p":"65000.12000000","q":"0.00150000","f":200,"l":201,"T":1762171200123,"m":true,"M":true},
             {"a":102,"p":"65000.13000000","q":"2.00000000","f":202,"l":202,"T":1762171200456,"m":false,"M":true}]
            """);
        using var client = NewClient(stub, new FakeTimeProvider(), out var rest);

        var trades = await rest.GetAggTradesFromAsync(Btc, 101, 500, TestContext.Current.CancellationToken);

        trades.Count.ShouldBe(2);
        trades[0].ShouldBe((101L, 65000.12m, 0.0015m, new DateTime(2025, 11, 3, 12, 0, 0, 123, DateTimeKind.Utc)));
        trades[0].TimeUtc.Kind.ShouldBe(DateTimeKind.Utc);
        trades[1].ShouldBe((102L, 65000.13m, 2m, new DateTime(2025, 11, 3, 12, 0, 0, 456, DateTimeKind.Utc)));
        stub.Requests.ShouldHaveSingleItem().PathAndQuery.ShouldBe("/api/v3/aggTrades?symbol=BTCUSDT&fromId=101&limit=500");
    }

    [Theory]
    [InlineData("exchangeInfo", "not json")]
    [InlineData("exchangeInfo", """{"timezone":"UTC"}""")]
    [InlineData("exchangeInfo", """{"symbols":[{"symbol":"BTCUSDT"}]}""")]
    [InlineData("klines", """{"code":0}""")]
    [InlineData("klines", """[[1762171200000,"1.0","2.0","0.5"]]""")]
    [InlineData("klines", """[[1762171200000,1.5,"2","0.5","1","1",1762171259999,"1",1,"0","0","0"]]""")]
    [InlineData("klines", """[[1762171200000,"1,5","2","0.5","1","1",1762171259999,"1",1,"0","0","0"]]""")]
    [InlineData("aggTrades", """[{"a":1,"q":"1.0","T":1762171200000}]""")]
    [InlineData("aggTrades", """[{"a":"1","p":"1.0","q":"1.0","T":1762171200000}]""")]
    [InlineData("aggTrades", """[{"a":1,"p":"1.0","q":"1.0","T":999999999999999999}]""")]
    public async Task MalformedPayload_ThrowsNonTransientApiException(string endpoint, string body)
    {
        var ct = TestContext.Current.CancellationToken;
        var stub = new StubHandler();
        stub.Enqueue(HttpStatusCode.OK, body);
        using var client = NewClient(stub, new FakeTimeProvider(), out var rest);
        var t = new DateTime(2025, 11, 3, 12, 0, 0, DateTimeKind.Utc);

        Func<Task> call = endpoint switch
        {
            "exchangeInfo" => () => rest.IsTradingSymbolAsync(Btc, ct),
            "klines" => () => rest.GetMinuteKlinesAsync(Btc, t, t, 1, ct),
            _ => () => rest.GetAggTradesFromAsync(Btc, 1, 1, ct),
        };
        var ex = await Should.ThrowAsync<BinanceApiException>(call);

        ex.Message.ShouldContain($"malformed response from {endpoint}");
        ex.IsTransient.ShouldBeFalse();
        ex.InnerException.ShouldNotBeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1001)]
    public async Task AggTrades_LimitOutOfRange_Throws(int limit)
    {
        var stub = new StubHandler();
        using var client = NewClient(stub, new FakeTimeProvider(), out var rest);

        await Should.ThrowAsync<ArgumentOutOfRangeException>(
            () => rest.GetAggTradesFromAsync(Btc, 1, limit, TestContext.Current.CancellationToken));
        stub.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task HighUsedWeight_DelaysNextCall()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider(Noon.AddSeconds(10.5));
        var stub = new StubHandler();
        stub.Enqueue(HttpStatusCode.OK, TradingInfo, usedWeight: 4801);
        stub.Enqueue(HttpStatusCode.OK, TradingInfo, usedWeight: 3);
        stub.Enqueue(HttpStatusCode.OK, TradingInfo, usedWeight: 4);
        using var client = NewClient(stub, clock, out var rest);

        (await rest.IsTradingSymbolAsync(Btc, ct)).ShouldBeTrue();

        // Above 4800 the next request waits for the next minute window (12:01:00), not a fixed 60 s.
        var second = rest.IsTradingSymbolAsync(Btc, ct);
        second.IsCompleted.ShouldBeFalse();
        stub.Requests.Count.ShouldBe(1);

        clock.Advance(TimeSpan.FromSeconds(49.499));
        second.IsCompleted.ShouldBeFalse();
        stub.Requests.Count.ShouldBe(1);

        clock.Advance(TimeSpan.FromMilliseconds(1));
        (await second.WaitAsync(Timeout, ct)).ShouldBeTrue();
        stub.Requests.Count.ShouldBe(2);

        // The pause has ended and the weight is low again: the third call goes straight through.
        (await rest.IsTradingSymbolAsync(Btc, ct).WaitAsync(Timeout, ct)).ShouldBeTrue();
        stub.Requests.Count.ShouldBe(3);
    }

    [Fact]
    public async Task HighUsedWeight_WithDateHeader_UsesServerMinuteBoundary()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider(Noon.AddSeconds(10.5));
        var stub = new StubHandler();

        // Binance's clock says 12:00:58, so its window resets in 2 s, whatever the local clock says.
        stub.Enqueue(HttpStatusCode.OK, TradingInfo, usedWeight: 4801, date: Noon.AddSeconds(58));
        stub.Enqueue(HttpStatusCode.OK, TradingInfo);
        using var client = NewClient(stub, clock, out var rest);

        (await rest.IsTradingSymbolAsync(Btc, ct)).ShouldBeTrue();
        var second = rest.IsTradingSymbolAsync(Btc, ct);

        clock.Advance(TimeSpan.FromMilliseconds(1999));
        second.IsCompleted.ShouldBeFalse();
        stub.Requests.Count.ShouldBe(1);

        clock.Advance(TimeSpan.FromMilliseconds(1));
        (await second.WaitAsync(Timeout, ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task UsedWeightAtLimit_DoesNotDelay()
    {
        var ct = TestContext.Current.CancellationToken;
        var stub = new StubHandler();
        stub.Enqueue(HttpStatusCode.OK, TradingInfo, usedWeight: 4800);
        stub.Enqueue(HttpStatusCode.OK, TradingInfo, usedWeight: 4801);
        using var client = NewClient(stub, new FakeTimeProvider(), out var rest);

        (await rest.IsTradingSymbolAsync(Btc, ct)).ShouldBeTrue();
        (await rest.IsTradingSymbolAsync(Btc, ct).WaitAsync(Timeout, ct)).ShouldBeTrue();

        stub.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Paused_CallerCancels_ThrowsOperationCanceledWithoutRequest()
    {
        var ct = TestContext.Current.CancellationToken;
        var stub = new StubHandler();
        stub.Enqueue(HttpStatusCode.OK, TradingInfo, usedWeight: 5000);
        using var client = NewClient(stub, new FakeTimeProvider(Noon), out var rest);
        (await rest.IsTradingSymbolAsync(Btc, ct)).ShouldBeTrue();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var paused = rest.GetAggTradesFromAsync(Btc, 1, 10, cts.Token);
        paused.IsCompleted.ShouldBeFalse();
        await cts.CancelAsync();

        (await CatchAsync(() => paused.WaitAsync(Timeout, ct))).ShouldBeAssignableTo<OperationCanceledException>();
        stub.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Status429_RespectsRetryAfter()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider(Noon.AddSeconds(10));
        var stub = new StubHandler();
        stub.Enqueue((HttpStatusCode)429, TooManyRequests, retryAfterSeconds: 7);
        stub.Enqueue(HttpStatusCode.OK, TradingInfo);
        stub.Enqueue(HttpStatusCode.OK, TradingInfo);
        using var client = NewClient(stub, clock, out var rest);

        var first = rest.IsTradingSymbolAsync(Btc, ct);
        first.IsCompleted.ShouldBeFalse();
        stub.Requests.Count.ShouldBe(1);

        // Another caller is held by the same pause instead of hitting the API during the back-off.
        var other = rest.IsTradingSymbolAsync(Btc, ct);
        other.IsCompleted.ShouldBeFalse();
        stub.Requests.Count.ShouldBe(1);

        clock.Advance(TimeSpan.FromMilliseconds(6999));
        first.IsCompleted.ShouldBeFalse();
        stub.Requests.Count.ShouldBe(1);

        clock.Advance(TimeSpan.FromMilliseconds(1));
        (await first.WaitAsync(Timeout, ct)).ShouldBeTrue();
        (await other.WaitAsync(Timeout, ct)).ShouldBeTrue();
        stub.Requests.Count.ShouldBe(3);
    }

    [Fact]
    public async Task Status429_WithoutRetryAfter_WaitsUntilNextMinute()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider(Noon.AddSeconds(10));
        var stub = new StubHandler();
        stub.Enqueue((HttpStatusCode)429, TooManyRequests);
        stub.Enqueue(HttpStatusCode.OK, TradingInfo);
        using var client = NewClient(stub, clock, out var rest);

        var call = rest.IsTradingSymbolAsync(Btc, ct);

        clock.Advance(TimeSpan.FromMilliseconds(49_999));
        call.IsCompleted.ShouldBeFalse();
        stub.Requests.Count.ShouldBe(1);

        clock.Advance(TimeSpan.FromMilliseconds(1));
        (await call.WaitAsync(Timeout, ct)).ShouldBeTrue();
        stub.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Status429_RetryAfterHttpDate_MeasuredFromResponseDate()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider(Noon.AddSeconds(10));
        var stub = new StubHandler();

        // The server clock is 5 minutes ahead; "retry at 12:05:30" with Date 12:05:00 means "in 30 s".
        stub.Enqueue((HttpStatusCode)429, TooManyRequests, date: Noon.AddMinutes(5), retryAfterDate: Noon.AddMinutes(5).AddSeconds(30));
        stub.Enqueue(HttpStatusCode.OK, TradingInfo);
        using var client = NewClient(stub, clock, out var rest);

        var call = rest.IsTradingSymbolAsync(Btc, ct);

        clock.Advance(TimeSpan.FromMilliseconds(29_999));
        call.IsCompleted.ShouldBeFalse();
        stub.Requests.Count.ShouldBe(1);

        clock.Advance(TimeSpan.FromMilliseconds(1));
        (await call.WaitAsync(Timeout, ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task Status429_Repeatedly_ThrowsTransientAfterRetries()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new TimerTrackingClock(Noon);
        var stub = new StubHandler();
        for (var i = 0; i < 4; i++)
        {
            stub.Enqueue((HttpStatusCode)429, TooManyRequests, retryAfterSeconds: 1);
        }

        using var client = NewClient(stub, clock, out var rest);

        var call = rest.IsTradingSymbolAsync(Btc, ct);

        // Each back-off is one Task.Delay on the clock: wait until the client is parked on it, then release it.
        for (var pause = 1; pause <= BinanceRestClient.MaxRateLimitRetries; pause++)
        {
            await clock.WaitForTimersAsync(pause, ct);
            stub.Requests.Count.ShouldBe(pause);
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        var ex = await Should.ThrowAsync<BinanceApiException>(() => call.WaitAsync(Timeout, ct));
        ex.StatusCode.ShouldBe((HttpStatusCode)429);
        ex.IsTransient.ShouldBeTrue();
        stub.Requests.Count.ShouldBe(BinanceRestClient.MaxRateLimitRetries + 1);
    }

    [Fact]
    public async Task Status418_ThrowsBannedAndBlocksCallsUntilBanEnds()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider(Noon);
        var stub = new StubHandler();
        stub.Enqueue((HttpStatusCode)418, """{"code":-1003,"msg":"Way too many requests; IP banned."}""", retryAfterSeconds: 120);
        stub.Enqueue(HttpStatusCode.OK, TradingInfo);
        using var client = NewClient(stub, clock, out var rest);

        var banned = await Should.ThrowAsync<BinanceBannedException>(() => rest.IsTradingSymbolAsync(Btc, ct));
        banned.BannedUntilUtc.ShouldBe(Noon.AddMinutes(2).UtcDateTime);

        // While banned, calls fail fast without touching the API (requests during a ban extend it).
        clock.Advance(TimeSpan.FromSeconds(119));
        await Should.ThrowAsync<BinanceBannedException>(() => rest.GetAggTradesFromAsync(Btc, 1, 10, ct));
        stub.Requests.Count.ShouldBe(1);

        clock.Advance(TimeSpan.FromSeconds(1));
        (await rest.IsTradingSymbolAsync(Btc, ct)).ShouldBeTrue();
        stub.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Status418_WithoutRetryAfter_BansForTwoMinutes()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider(Noon.AddSeconds(10));
        var stub = new StubHandler();
        stub.Enqueue((HttpStatusCode)418, "");
        using var client = NewClient(stub, clock, out var rest);

        var banned = await Should.ThrowAsync<BinanceBannedException>(() => rest.IsTradingSymbolAsync(Btc, ct));

        banned.BannedUntilUtc.ShouldBe(Noon.AddSeconds(10).AddMinutes(2).UtcDateTime);
    }

    [Fact]
    public async Task AddBinanceRest_ResiliencePipeline_LeavesRateLimitStatusesToClient()
    {
        var services = new ServiceCollection();
        services.AddBinanceRest(Config(BaseUrl));
        await using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptionsMonitor<HttpStandardResilienceOptions>>()
            .Get(BinanceServiceCollectionExtensions.RestResilienceOptionsName);

        foreach (var (status, transient) in new[]
        {
            ((HttpStatusCode)429, false),
            ((HttpStatusCode)418, false),
            (HttpStatusCode.ServiceUnavailable, true),
            (HttpStatusCode.InternalServerError, true),
            (HttpStatusCode.RequestTimeout, true),
            (HttpStatusCode.OK, false),
        })
        {
            using var response = new HttpResponseMessage(status);
            (await ShouldHandleAsync(options, Outcome.FromResult(response)))
                .ShouldBe((transient, transient), $"retry / circuit breaker for {(int)status}");
        }

        (await ShouldHandleAsync(options, Outcome.FromException<HttpResponseMessage>(new HttpRequestException("refused"))))
            .ShouldBe((true, true));
    }

    [Fact]
    public async Task AddBinanceRest_HandlerChain_ResilienceWrapsAttemptHandlerOverPooledSockets()
    {
        var services = new ServiceCollection();
        services.AddBinanceRest(Config(BaseUrl));
        await using var provider = services.BuildServiceProvider();
        var name = BinanceServiceCollectionExtensions.RestClientName;

        provider.GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>().Get(name)
            .HandlerLifetime.ShouldBe(System.Threading.Timeout.InfiniteTimeSpan);

        var chain = new List<HttpMessageHandler>();
        for (HttpMessageHandler? h = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(name);
            h is not null;
            h = (h as DelegatingHandler)?.InnerHandler)
        {
            chain.Add(h);
        }

        var names = chain.Select(h => h.GetType().Name).ToList();
        var resilience = names.FindIndex(n => n.Contains("Resilience", StringComparison.Ordinal));
        resilience.ShouldBeGreaterThanOrEqualTo(0, string.Join(" > ", names));
        names.IndexOf("BinanceAttemptHandler").ShouldBeGreaterThan(resilience, string.Join(" > ", names));
        chain[^1].ShouldBeOfType<SocketsHttpHandler>().PooledConnectionLifetime.ShouldBe(TimeSpan.FromMinutes(2));
    }

    [Fact]
    public async Task AddBinanceRest_StalledBody_CutOffByAttemptTimeoutAndReportedTransient()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new TimerTrackingClock(Noon);
        var stub = new StubHandler();
        for (var i = 0; i < 5; i++)
        {
            stub.EnqueueStalledBody();
        }

        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        services.AddBinanceRest(Config(BaseUrl));
        services.ConfigureBinanceRestResilience(o => o.Retry.Delay = TimeSpan.Zero);
        services.AddHttpClient(BinanceServiceCollectionExtensions.RestClientName).ConfigurePrimaryHttpMessageHandler(() => stub);
        await using var provider = services.BuildServiceProvider();
        var rest = provider.GetRequiredService<BinanceRestClient>();

        var call = rest.GetAggTradesFromAsync(Btc, 1, 10, ct);

        // Polly's timeouts run on the DI TimeProvider: step past each 10 s attempt timeout until the 30 s total
        // timeout gives up, waiting after each step for the next attempt's timer (or for the call to end).
        while (!call.IsCompleted)
        {
            var seen = clock.TimersCreated;
            clock.Advance(TimeSpan.FromSeconds(10));
            await Task.WhenAny(call, clock.WaitForTimersAsync(seen + 1, ct)).WaitAsync(Timeout, ct);
        }

        var ex = await Should.ThrowAsync<BinanceApiException>(() => call);
        ex.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        ex.IsTransient.ShouldBeTrue();
        ex.InnerException.ShouldBeOfType<TimeoutRejectedException>();
        stub.Requests.Count.ShouldBeGreaterThanOrEqualTo(2, "the stalled attempt was cut off and retried");
    }

    [Fact]
    public async Task AddBinanceRest_AgainstFakeServer_ServesAllPorts()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await FakeBinanceServer.StartAsync();
        var t0 = new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);
        server.AddTrade("BTCUSDT", 1, 65000.10m, t0.AddSeconds(1), 0.5m);
        server.AddTrade("BTCUSDT", 2, 65002.20m, t0.AddSeconds(30), 1.5m);
        server.AddTrade("BTCUSDT", 3, 64999.90m, t0.AddMinutes(1).AddSeconds(5), 2m);
        server.SetSymbolStatus("ADAUSDT", "BREAK");

        var services = new ServiceCollection();
        services.AddBinanceRest(Config(server.RestBaseUrl));
        await using var provider = services.BuildServiceProvider();
        var metadata = provider.GetRequiredService<IExchangeMetadata>();
        var klines = provider.GetRequiredService<IKlineSource>();
        var aggTrades = provider.GetRequiredService<IAggTradeSource>();

        metadata.ShouldBeOfType<BinanceExchangeMetadata>();
        (await metadata.IsTradingSymbolAsync(Btc, ct)).ShouldBeTrue();
        (await metadata.IsTradingSymbolAsync(Symbol("ADAUSDT"), ct)).ShouldBeFalse();
        (await metadata.IsTradingSymbolAsync(Symbol("NOPEUSDT"), ct)).ShouldBeFalse();

        var candles = await klines.GetMinuteKlinesAsync(Btc, t0, t0.AddMinutes(5), 1000, ct);
        candles.ShouldBe(
        [
            new Kline(t0, 65000.10m, 65002.20m, 65000.10m, 65002.20m, 2m, 65000.10m * 0.5m + 65002.20m * 1.5m),
            new Kline(t0.AddMinutes(1), 64999.90m, 64999.90m, 64999.90m, 64999.90m, 2m, 64999.90m * 2m),
        ]);

        var trades = await aggTrades.GetAggTradesFromAsync(Btc, 2, 1000, ct);
        trades.ShouldBe(
        [
            (2L, 65002.20m, 1.5m, t0.AddSeconds(30)),
            (3L, 64999.90m, 2m, t0.AddMinutes(1).AddSeconds(5)),
        ]);
    }

    private static async Task<(bool Retry, bool CircuitBreaker)> ShouldHandleAsync(
        HttpStandardResilienceOptions options, Outcome<HttpResponseMessage> outcome)
    {
        var context = ResilienceContextPool.Shared.Get(TestContext.Current.CancellationToken);
        try
        {
            var retry = await options.Retry.ShouldHandle(new RetryPredicateArguments<HttpResponseMessage>(context, outcome, 0));
            var breaker = await options.CircuitBreaker.ShouldHandle(new CircuitBreakerPredicateArguments<HttpResponseMessage>(context, outcome));
            return (retry, breaker);
        }
        finally
        {
            ResilienceContextPool.Shared.Return(context);
        }
    }

    private static async Task<Exception?> CatchAsync(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static SymbolName Symbol(string raw) =>
        SymbolName.TryCreate(raw, out var name) ? name : throw new ArgumentException(raw, nameof(raw));

    private static IConfiguration Config(Uri restBaseUrl) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["Binance:RestBaseUrl"] = restBaseUrl.AbsoluteUri })
        .Build();

    private static HttpClient NewClient(StubHandler stub, TimeProvider clock, out BinanceRestClient rest)
    {
        var http = new HttpClient(stub) { BaseAddress = BaseUrl };
        rest = new BinanceRestClient(http, new BinanceWeightGuard(clock));
        return http;
    }

    // InvariantGlobalization is on, so ICU's bg-BG data is unavailable; this reproduces its number format
    // (comma decimal separator, no-break-space group separator), which is what breaks culture-sensitive parsing.
    private static CultureInfo BulgarianNumberCulture()
    {
        var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        culture.NumberFormat.NumberDecimalSeparator = ",";
        culture.NumberFormat.NumberGroupSeparator = " ";
        culture.NumberFormat.CurrencyDecimalSeparator = ",";
        culture.NumberFormat.CurrencyGroupSeparator = " ";
        return culture;
    }

    /// <summary>
    /// A <see cref="FakeTimeProvider"/> that counts the timers created on it (every <c>Task.Delay</c> and every
    /// timed <c>CancellationTokenSource</c>), so a test can wait until code is parked on the clock before advancing it.
    /// </summary>
    private sealed class TimerTrackingClock(DateTimeOffset start) : FakeTimeProvider(start)
    {
        private readonly Lock _gate = new();
        private int _timers;
        private TaskCompletionSource _created = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int TimersCreated
        {
            get
            {
                lock (_gate)
                {
                    return _timers;
                }
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            TaskCompletionSource created;
            lock (_gate)
            {
                _timers++;
                created = _created;
                _created = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            created.TrySetResult();
            return timer;
        }

        public async Task WaitForTimersAsync(int count, CancellationToken ct)
        {
            while (true)
            {
                Task next;
                lock (_gate)
                {
                    if (_timers >= count)
                    {
                        return;
                    }

                    next = _created.Task;
                }

                await next.WaitAsync(Timeout, ct);
            }
        }
    }

    /// <summary>Answers requests from a queue of canned responses and records every request URI.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Lock _gate = new();
        private readonly Queue<Func<CancellationToken, HttpResponseMessage>> _responses = new();
        private readonly List<Uri> _requests = [];

        public IReadOnlyList<Uri> Requests
        {
            get
            {
                lock (_gate)
                {
                    return [.. _requests];
                }
            }
        }

        public void Enqueue(
            HttpStatusCode status,
            string body,
            int? usedWeight = null,
            int? retryAfterSeconds = null,
            DateTimeOffset? date = null,
            DateTimeOffset? retryAfterDate = null) => Add(_ =>
        {
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            if (usedWeight is { } weight)
            {
                response.Headers.Add("x-mbx-used-weight-1m", weight.ToString(CultureInfo.InvariantCulture));
            }

            if (retryAfterSeconds is { } seconds)
            {
                response.Headers.Add("Retry-After", seconds.ToString(CultureInfo.InvariantCulture));
            }

            if (retryAfterDate is { } until)
            {
                response.Headers.RetryAfter = new RetryConditionHeaderValue(until);
            }

            response.Headers.Date = date;
            return response;
        });

        public void EnqueueThrow(Func<CancellationToken, Exception> exception) => Add(ct => throw exception(ct));

        /// <summary>200 with headers, then a body that never arrives (until the read is cancelled).</summary>
        public void EnqueueStalledBody() => Add(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new StalledStream()),
        });

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Func<CancellationToken, HttpResponseMessage> next;
            lock (_gate)
            {
                _requests.Add(request.RequestUri!);
                next = _responses.Count > 0
                    ? _responses.Dequeue()
                    : throw new InvalidOperationException($"No canned response left for {request.RequestUri}");
            }

            try
            {
                var response = next(cancellationToken);
                response.RequestMessage = request;
                return Task.FromResult(response);
            }
            catch (Exception ex)
            {
                return Task.FromException<HttpResponseMessage>(ex);
            }
        }

        private void Add(Func<CancellationToken, HttpResponseMessage> response)
        {
            lock (_gate)
            {
                _responses.Enqueue(response);
            }
        }
    }

    private sealed class StalledStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(System.Threading.Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
