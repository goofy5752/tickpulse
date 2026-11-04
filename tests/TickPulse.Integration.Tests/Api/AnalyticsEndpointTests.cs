using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using TickPulse.Application.Abstractions;
using TickPulse.Application.Ingestion;
using TickPulse.Domain;
using TickPulse.Integration.Tests.Fixtures;
using static TickPulse.Integration.Tests.Api.ProblemReader;

namespace TickPulse.Integration.Tests.Api;

/// <summary>
/// The public analytics routes end to end: the real host, TimescaleDB, ticks stored through <see cref="ITickWriter"/>
/// and candles through <see cref="ICandleWriter"/>. The app clock is pinned to <see cref="Now"/>; every expected value
/// below is worked out by hand from the seeded rows.
/// </summary>
public sealed class AnalyticsEndpointTests(ApiFactory api) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    // Wednesday 2026-10-07 12:34:56 UTC. The last complete 5m bucket is [12:25, 12:30); the last complete day is 10-06.
    private static readonly DateTime Now = new(2026, 10, 7, 12, 34, 56, DateTimeKind.Utc);

    private static readonly XNamespace Schema = "https://tickpulse.dev/schema/v1";
    private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private int Btc => SymbolId("BTCUSDT");

    private int Ada => SymbolId("ADAUSDT");

    public async ValueTask InitializeAsync()
    {
        await api.Database.ResetAsync();
        api.Clock.AdjustTime(new DateTimeOffset(Now));
        var live = api.Services.GetRequiredService<IngestionState>();
        foreach (var symbol in live.Latest.Keys.ToList())
        {
            live.ForgetSymbol(symbol);
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // ---- 24h average ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Avg_ReturnsExactAverageAndCoverage()
    {
        await SeedAverageTicksAsync(Btc);

        using var response = await GetAsync("/api/BTCUSDT/24hAvgPrice");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        MediaType(response).ShouldBe("application/json");
        using var json = await JsonAsync(response);
        var root = json.RootElement;
        root.GetProperty("symbol").GetString().ShouldBe("BTCUSDT");

        // Inside [now − 24 h, now]: 1234567890123.10000001 + .20000002 + .30000003 + .40000004 = 4938271560493.00000010;
        // / 4 = 1234567890123.250000025, a tie at 8 dp that banker's rounding takes down to …25000002. A double would
        // have lost everything after ".25".
        var average = root.GetProperty("averagePrice");
        average.ValueKind.ShouldBe(JsonValueKind.Number);
        average.GetRawText().ShouldBe("1234567890123.25000002");
        average.GetDecimal().ShouldBe(1_234_567_890_123.25000002m);

        // The first tick sits exactly on now − 24 h (inclusive); the last 30 s before now. The ticks 1 ms before the
        // window and 1 s after now are excluded.
        root.GetProperty("from").GetString().ShouldBe("2026-10-06T12:34:56Z");
        root.GetProperty("to").GetString().ShouldBe("2026-10-07T12:34:26Z");
        root.GetProperty("coveredSeconds").GetInt64().ShouldBe(86_370);
        root.GetProperty("sampleCount").GetInt64().ShouldBe(4);
        root.GetProperty("windowHours").GetInt32().ShouldBe(24);
        Names(root).ShouldBe(["symbol", "averagePrice", "from", "to", "coveredSeconds", "sampleCount", "windowHours"]);
    }

    [Theory]
    [InlineData("btcusdt")]
    [InlineData("BtcUsdt")]
    [InlineData("%20BTCUSDT%20")]
    public async Task Avg_LowercaseSymbol_Works(string symbol)
    {
        await WriteTicksAsync(Tick(Btc, 1, 42.5m, Now.AddMinutes(-5)));

        using var response = await GetAsync($"/api/{symbol}/24hAvgPrice");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var json = await JsonAsync(response);
        json.RootElement.GetProperty("symbol").GetString().ShouldBe("BTCUSDT");
        json.RootElement.GetProperty("averagePrice").GetDecimal().ShouldBe(42.5m);
    }

    [Theory]
    [InlineData("BTC-USDT", "BTC-USDT")]
    [InlineData("ADMIN", "ADMIN")]
    [InlineData("nopeusdt", "NOPEUSDT")]
    public async Task Avg_GarbageSymbol_Returns404Problem(string symbol, string echoed)
    {
        using var response = await GetAsync($"/api/{symbol}/24hAvgPrice");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var problem = await ReadAsync(response);
        problem.Type.ShouldBe(TypeBase + "unknown-symbol");
        problem.Status.ShouldBe(404);
        problem.Detail.ShouldBe($"Symbol '{echoed}' is not monitored.");
        problem.TraceId.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Avg_InactiveSymbolWithData_Returns200()
    {
        await WriteTicksAsync(Tick(Ada, 1, 0.25m, Now.AddHours(-2)), Tick(Ada, 2, 0.26m, Now.AddHours(-1)));
        await DeactivateAsync("ADAUSDT");

        using var response = await GetAsync("/api/ADAUSDT/24hAvgPrice");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var json = await JsonAsync(response);
        json.RootElement.GetProperty("averagePrice").GetDecimal().ShouldBe(0.255m);
        json.RootElement.GetProperty("sampleCount").GetInt64().ShouldBe(2);
    }

    [Fact]
    public async Task Avg_NoTicksInWindow_Returns404NoData()
    {
        // Only a tick from before the window.
        await WriteTicksAsync(Tick(Btc, 1, 1m, Now.AddHours(-25)));

        using var response = await GetAsync("/api/BTCUSDT/24hAvgPrice");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var problem = await ReadAsync(response);
        problem.Type.ShouldBe(TypeBase + "no-data");
        problem.Detail.ShouldBe("No trades were collected for 'BTCUSDT' in the last 24 hours.");
    }

    [Fact]
    public async Task Avg_TickAtNow_IsIncluded_AndBackfillCandlesAreIgnored()
    {
        // Ticks at now − 1 h (10) and exactly at now (20), plus backfilled minutes inside the window, one of them in
        // a minute that also holds a streamed tick (the stream row replaces it) and one in a minute with none.
        await WriteCandlesAsync(Btc, Candle(At(10, 0), 1_000m), Candle(At(11, 34), 1_000m));
        await WriteTicksAsync(Tick(Btc, 1, 10m, Now.AddHours(-1)), Tick(Btc, 2, 20m, Now));

        using var response = await GetAsync("/api/BTCUSDT/24hAvgPrice");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var json = await JsonAsync(response);
        var root = json.RootElement;

        // (10 + 20) / 2 = 15 over exactly two samples: the 1000-closes of the backfill candles never count.
        root.GetProperty("averagePrice").GetDecimal().ShouldBe(15m);
        root.GetProperty("sampleCount").GetInt64().ShouldBe(2);
        root.GetProperty("from").GetString().ShouldBe("2026-10-07T11:34:56Z");
        root.GetProperty("to").GetString().ShouldBe("2026-10-07T12:34:56Z");
        root.GetProperty("coveredSeconds").GetInt64().ShouldBe(3_600);
    }

    [Theory]
    [MemberData(nameof(NegotiationTests.SuccessMatrix), MemberType = typeof(NegotiationTests))]
    public async Task Avg_NegotiationMatrix(string? accept, HttpStatusCode status, string mediaType)
    {
        await SeedAverageTicksAsync(Btc);

        using var response = await GetAsync("/api/BTCUSDT/24hAvgPrice", accept);

        response.StatusCode.ShouldBe(status);
        MediaType(response).ShouldBe(mediaType);
        response.Headers.Vary.ShouldContain("Accept");
        switch (mediaType)
        {
            case "application/json":
                using (var json = await JsonAsync(response))
                {
                    json.RootElement.GetProperty("symbol").GetString().ShouldBe("BTCUSDT");
                    json.RootElement.GetProperty("averagePrice").GetRawText().ShouldBe("1234567890123.25000002");
                    json.RootElement.GetProperty("from").GetString().ShouldBe("2026-10-06T12:34:56Z");
                }

                break;
            case "application/xml" or "text/xml":
                var root = await XmlAsync(response);
                root.Name.ShouldBe(Schema + "averagePrice");
                root.Element(Schema + "symbol")!.Value.ShouldBe("BTCUSDT");
                root.Element(Schema + "averagePrice")!.Value.ShouldBe("1234567890123.25000002");
                root.Element(Schema + "from")!.Value.ShouldBe("2026-10-06T12:34:56Z");
                root.Element(Schema + "coveredSeconds")!.Value.ShouldBe("86370");
                break;
            default:
                var problem = await ReadAsync(response);
                problem.Type.ShouldBe(TypeBase + "not-acceptable");
                problem.Status.ShouldBe(406);
                break;
        }
    }

    // ---- SMA --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Sma_NoS_UsesLastCompleteBuckets()
    {
        await SeedFiveMinuteCandlesAsync();

        using var response = await GetAsync("/api/BTCUSDT/SimpleMovingAverage?n=3&p=5m");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var json = await JsonAsync(response);
        var root = json.RootElement;
        root.GetProperty("symbol").GetString().ShouldBe("BTCUSDT");
        root.GetProperty("period").GetString().ShouldBe("5m");
        root.GetProperty("requestedDataPoints").GetInt32().ShouldBe(3);
        root.GetProperty("actualDataPoints").GetInt32().ShouldBe(3);

        // The last three complete 5m buckets end at 12:30: [12:15, 12:30). Their closes are those of their latest
        // minutes: 12:19 → 11, 12:20 → 20, 12:29 → 32.5. (11 + 20 + 32.5) / 3 = 21.1666… → 21.16666667.
        // 12:10 lies before the window; 12:30 and 12:34 are in the current, incomplete bucket.
        root.GetProperty("value").GetRawText().ShouldBe("21.16666667");
        root.GetProperty("windowStart").GetString().ShouldBe("2026-10-07T12:15:00Z");
        root.GetProperty("windowEnd").GetString().ShouldBe("2026-10-07T12:30:00Z");
        root.GetProperty("referenceTime").GetString().ShouldBe("2026-10-07T12:34:56Z");
        root.GetProperty("startParameter").ValueKind.ShouldBe(JsonValueKind.Null);
        DataPoints(root).ShouldBe(
        [
            ("2026-10-07T12:15:00Z", 11m),
            ("2026-10-07T12:20:00Z", 20m),
            ("2026-10-07T12:25:00Z", 32.5m),
        ]);
    }

    [Theory]
    [InlineData("2026-10-02T00:00:00Z", "2026-10-02T00:00:00Z")]
    [InlineData("2026-10-01T00:00:01Z", "2026-10-01T00:00:01Z")]   // not a boundary: the window starts at the next day
    public async Task Sma_WithS_StartsAtS(string s, string startParameter)
    {
        await SeedDailyCandlesAsync();

        using var response = await GetAsync($"/api/BTCUSDT/SimpleMovingAverage?n=3&p=1d&s={Uri.EscapeDataString(s)}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var json = await JsonAsync(response);
        var root = json.RootElement;

        // Days 10-02 (close of 23:59 → 102), 10-03 (104) and 10-04 (107); 10-05 is beyond n = 3.
        // (102 + 104 + 107) / 3 = 104.3333… → 104.33333333.
        root.GetProperty("value").GetRawText().ShouldBe("104.33333333");
        root.GetProperty("windowStart").GetString().ShouldBe("2026-10-02T00:00:00Z");
        root.GetProperty("windowEnd").GetString().ShouldBe("2026-10-05T00:00:00Z");
        root.GetProperty("startParameter").GetString().ShouldBe(startParameter);
        DataPoints(root).Select(p => p.Close).ShouldBe([102m, 104m, 107m]);
    }

    [Fact]
    public async Task Sma_SWithOffset_IsConvertedToUtc()
    {
        await SeedDailyCandlesAsync();

        using var response = await GetAsync($"/api/BTCUSDT/SimpleMovingAverage?n=2&p=1d&s={Uri.EscapeDataString("2026-10-01T03:00:00+03:00")}");

        await ShouldBeTwoDaysFromOctoberFirstAsync(response);
    }

    [Fact]
    public async Task Sma_UnencodedPlusOffset_IsAccepted()
    {
        await SeedDailyCandlesAsync();

        // The '+' is sent as is, so the server's query decoding turns it into a space.
        using var response = await GetAsync("/api/BTCUSDT/SimpleMovingAverage?n=2&p=1d&s=2026-10-01T03:00:00+03:00");

        await ShouldBeTwoDaysFromOctoberFirstAsync(response);
    }

    [Fact]
    public async Task Sma_SWithoutOffset_IsUtc()
    {
        await SeedDailyCandlesAsync();

        using var response = await GetAsync("/api/BTCUSDT/SimpleMovingAverage?n=2&p=1d&s=2026-10-01T00:00:00");

        await ShouldBeTwoDaysFromOctoberFirstAsync(response);
    }

    [Theory]
    [InlineData("2026-10-07T12:34:57Z", null)]
    [InlineData("2026-10-07T12:34:57Z", "application/xml")]   // a problem raised by the service, negotiated as XML
    [InlineData("9999-12-31T23:59:59-01:00", null)]           // UTC 10000-01-01T00:59:59: past DateTime.MaxValue, still "future"
    public async Task Sma_FutureS_Returns400(string s, string? accept)
    {
        await SeedDailyCandlesAsync();

        using var response = await GetAsync($"/api/BTCUSDT/SimpleMovingAverage?n=2&p=1d&s={Uri.EscapeDataString(s)}", accept);

        await ShouldBeValidationAsync(response, "s", "s must not be in the future.", accept);
    }

    [Theory]
    [InlineData("yesterday")]
    [InlineData("0001-01-01T00:00:00+03:00")]   // UTC would be before 0001-01-01T00:00:00
    [InlineData("2026-10-01T00:00:00+0300")]
    [InlineData("2026-10-01T00:00:00+15:00")]
    public async Task Sma_UnparseableS_Returns400(string s)
    {
        using var response = await GetAsync($"/api/BTCUSDT/SimpleMovingAverage?n=2&p=1d&s={Uri.EscapeDataString(s)}");

        await ShouldBeValidationAsync(response, "s", "s must be an ISO-8601 timestamp such as 2026-10-01T00:00:00Z.");
    }

    [Theory]
    [InlineData("1h")]
    [InlineData("1W")]
    [InlineData("5")]
    public async Task Sma_InvalidP_Returns400(string p)
    {
        using var response = await GetAsync($"/api/BTCUSDT/SimpleMovingAverage?n=2&p={p}");

        await ShouldBeValidationAsync(response, "p", "p must be one of 1w, 1d, 30m, 5m, 1m.");
    }

    [Theory]
    [InlineData("n=2")]
    [InlineData("n=2&p=")]
    public async Task Sma_MissingP_Returns400(string query)
    {
        using var response = await GetAsync($"/api/BTCUSDT/SimpleMovingAverage?{query}", "application/xml");

        await ShouldBeValidationAsync(response, "p", "p is required.", "application/xml");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    [InlineData(1001)]
    public async Task Sma_NZero_Returns400(int n)
    {
        using var response = await GetAsync($"/api/BTCUSDT/SimpleMovingAverage?n={n}&p=1m");

        await ShouldBeValidationAsync(response, "n", "n must be between 1 and 1000.");
    }

    [Theory]
    [InlineData("p=1m")]
    [InlineData("n=&p=1m")]
    public async Task Sma_MissingN_Returns400(string query)
    {
        using var response = await GetAsync($"/api/BTCUSDT/SimpleMovingAverage?{query}");

        await ShouldBeValidationAsync(response, "n", "n is required.");
    }

    [Theory]
    [InlineData("/api/BTCUSDT/SimpleMovingAverage?n=abc&p=1m", null, "n")]
    [InlineData("/api/BTCUSDT/SimpleMovingAverage?n=abc&p=1m", "application/xml", "n")]
    [InlineData("/api/BTCUSDT/candles?p=1m&limit=abc", null, "limit")]
    [InlineData("/api/BTCUSDT/candles?p=1m&limit=abc", "application/xml", "limit")]
    public async Task UnbindableParameter_ReturnsValidationProblem(string path, string? accept, string parameter)
    {
        using var response = await GetAsync(path, accept);

        // Model binding fails before the action runs; the problem comes from InvalidModelStateResponseFactory.
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        MediaType(response).ShouldBe(accept is null ? JsonMediaType : XmlMediaType);
        var problem = await ReadAsync(response);
        problem.Type.ShouldBe(TypeBase + "validation");
        problem.Title.ShouldBe("Validation failed");
        problem.Status.ShouldBe(400);
        problem.Detail.ShouldBe("One or more request parameters are invalid.");
        problem.TraceId.ShouldNotBeNullOrWhiteSpace();
        var errors = Errors(problem);
        errors.Keys.ShouldBe([parameter]);
        errors[parameter].ShouldBe(["The value 'abc' is not valid."]);
    }

    [Fact]
    public async Task Sma_FewerPoints_ReportsActual()
    {
        await SeedFiveMinuteCandlesAsync();

        using var response = await GetAsync("/api/BTCUSDT/SimpleMovingAverage?n=10&p=5m");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var json = await JsonAsync(response);
        var root = json.RootElement;

        // Ten buckets [11:40, 12:30) hold only four non-empty ones: 12:10 → 50, 12:15 → 11, 12:20 → 20, 12:25 → 32.5.
        // (50 + 11 + 20 + 32.5) / 4 = 28.375.
        root.GetProperty("requestedDataPoints").GetInt32().ShouldBe(10);
        root.GetProperty("actualDataPoints").GetInt32().ShouldBe(4);
        root.GetProperty("value").GetDecimal().ShouldBe(28.375m);
        root.GetProperty("windowStart").GetString().ShouldBe("2026-10-07T11:40:00Z");
        DataPoints(root).Select(p => p.Close).ShouldBe([50m, 11m, 20m, 32.5m]);
    }

    [Theory]
    [InlineData("BTCUSDT", "n=5&p=1m")]                          // no candles at all
    [InlineData("BTCUSDT", "n=5&p=1d&s=2026-10-07T00:00:01Z")]   // no complete day starts at or after s yet
    [InlineData("BTCUSDT", "n=5&p=1w&s=0001-01-01")]             // DateTime.MinValue (Npgsql: -infinity): an empty, valid window
    [InlineData("BTCUSDT", "n=1000&p=1m&s=0001-01-01T00:00:00Z")]
    public async Task Sma_NoData_Returns404(string symbol, string query)
    {
        using var response = await GetAsync($"/api/{symbol}/SimpleMovingAverage?{query}");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var problem = await ReadAsync(response);
        problem.Type.ShouldBe(TypeBase + "no-data");
        problem.Status.ShouldBe(404);
    }

    [Fact]
    public async Task Sma_UnknownSymbol_Returns404()
    {
        using var response = await GetAsync("/api/NOPEUSDT/SimpleMovingAverage?n=5&p=1m");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ReadAsync(response)).Type.ShouldBe(TypeBase + "unknown-symbol");
    }

    [Fact]
    public async Task Sma_Xml_HasSameFields()
    {
        await SeedFiveMinuteCandlesAsync();

        using var jsonResponse = await GetAsync("/api/BTCUSDT/SimpleMovingAverage?n=3&p=5m");
        using var xmlResponse = await GetAsync("/api/BTCUSDT/SimpleMovingAverage?n=3&p=5m", "application/xml");

        MediaType(xmlResponse).ShouldBe("application/xml");
        using var json = await JsonAsync(jsonResponse);
        var xml = await XmlAsync(xmlResponse);

        // The same members in the same order and with the same names: JSON properties and XML elements (in the schema
        // namespace) alike.
        string[] members =
        [
            "symbol", "period", "requestedDataPoints", "actualDataPoints", "value", "windowStart", "windowEnd",
            "referenceTime", "startParameter", "dataPoints",
        ];
        Names(json.RootElement).ShouldBe(members);
        xml.Name.ShouldBe(Schema + "sma");
        xml.Elements().Select(e => e.Name.NamespaceName).Distinct().ShouldBe([Schema.NamespaceName]);
        xml.Elements().Select(e => e.Name.LocalName).ShouldBe(members);

        // And the same values.
        foreach (var member in members.Where(m => m is not ("dataPoints" or "startParameter")))
        {
            xml.Element(Schema + member)!.Value.ShouldBe(Scalar(json.RootElement.GetProperty(member)), member);
        }

        xml.Element(Schema + "startParameter")!.Attribute(Xsi + "nil")!.Value.ShouldBe("true");

        var points = xml.Element(Schema + "dataPoints")!.Elements().ToList();
        points.Select(p => p.Name).ShouldAllBe(n => n == Schema + "dataPoint");
        var jsonPoints = json.RootElement.GetProperty("dataPoints").EnumerateArray().ToList();
        points.Count.ShouldBe(jsonPoints.Count);
        for (var i = 0; i < points.Count; i++)
        {
            Names(jsonPoints[i]).ShouldBe(["bucketStart", "close"]);
            points[i].Elements().Select(e => e.Name.LocalName).ShouldBe(["bucketStart", "close"]);
            points[i].Element(Schema + "bucketStart")!.Value.ShouldBe(jsonPoints[i].GetProperty("bucketStart").GetString());
            points[i].Element(Schema + "close")!.Value.ShouldBe(jsonPoints[i].GetProperty("close").GetRawText());
        }
    }

    // ---- symbols ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Symbols_ListsSeeded()
    {
        // BTCUSDT: live price 61500; the close at or before now − 24 h (10-06 12:34:56) is that of 12:30, 60000 → +2.50 %.
        await WriteCandlesAsync(Btc, Candle(Day(6, 6, 0), 1m), Candle(Day(6, 12, 30), 60_000m), Candle(Day(6, 12, 35), 2m));
        api.Services.GetRequiredService<IngestionState>().RecordBatch(1, 0, [new LivePrice("BTCUSDT", 61_500m, Now.AddSeconds(-1))]);

        // ADAUSDT, inactive: no live price, so the last close (0.3) is the latest; a day ago (12:25, inside the
        // tolerance) it was 0.4 → −25.00 %.
        await WriteCandlesAsync(Ada, Candle(Day(6, 12, 25), 0.4m), Candle(At(11, 0), 0.3m));
        await DeactivateAsync("ADAUSDT");

        // ETHUSDT has no data at all.
        using var response = await GetAsync("/api/symbols");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var json = await JsonAsync(response);
        var rows = json.RootElement.EnumerateArray().ToList();
        rows.Select(r => r.GetProperty("symbol").GetString()).ShouldBe(["BTCUSDT", "ADAUSDT", "ETHUSDT"]);
        Names(rows[0]).ShouldBe(["symbol", "isActive", "latestPrice", "change24hPercent", "backfillStatus"]);

        rows[0].GetProperty("isActive").GetBoolean().ShouldBeTrue();
        rows[0].GetProperty("latestPrice").GetDecimal().ShouldBe(61_500m);
        rows[0].GetProperty("change24hPercent").GetDecimal().ShouldBe(2.5m);
        rows[0].GetProperty("backfillStatus").GetString().ShouldBe("NotRequested");

        rows[1].GetProperty("isActive").GetBoolean().ShouldBeFalse();
        rows[1].GetProperty("latestPrice").GetDecimal().ShouldBe(0.3m);
        rows[1].GetProperty("change24hPercent").GetDecimal().ShouldBe(-25m);

        rows[2].GetProperty("isActive").GetBoolean().ShouldBeTrue();
        rows[2].GetProperty("latestPrice").ValueKind.ShouldBe(JsonValueKind.Null);
        rows[2].GetProperty("change24hPercent").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Symbols_StaleFallback_HasNullChange()
    {
        // ETHUSDT's only candle is 30 h old: it is both the latest price and the close a day ago, so there is no
        // 24 h change to report (rather than a misleading 0.00 %).
        await WriteCandlesAsync(SymbolId("ETHUSDT"), Candle(Day(6, 6, 0), 2_500m));

        using var response = await GetAsync("/api/symbols");

        using var json = await JsonAsync(response);
        var eth = json.RootElement.EnumerateArray().Single(r => r.GetProperty("symbol").GetString() == "ETHUSDT");
        eth.GetProperty("latestPrice").GetDecimal().ShouldBe(2_500m);
        eth.GetProperty("change24hPercent").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Symbols_BackfillBelowTheWindow_HasNullChange()
    {
        // A backfill in progress has stored weeks-old minutes but has not reached now − 24 h yet; the stream has
        // stored the last minutes. The newest close at or before now − 24 h is the backfill frontier, 40 days back.
        await WriteCandlesAsync(Btc, Candle(new DateTime(2026, 8, 28, 12, 34, 0, DateTimeKind.Utc), 40_000m), Candle(At(12, 33), 61_000m));
        api.Services.GetRequiredService<IngestionState>().RecordBatch(1, 0, [new LivePrice("BTCUSDT", 61_500m, Now.AddSeconds(-1))]);

        using var response = await GetAsync("/api/symbols");

        using var json = await JsonAsync(response);
        var btc = json.RootElement.EnumerateArray().Single(r => r.GetProperty("symbol").GetString() == "BTCUSDT");
        btc.GetProperty("latestPrice").GetDecimal().ShouldBe(61_500m);
        btc.GetProperty("change24hPercent").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Symbols_Xml_IsAListOfSymbolElements()
    {
        using var response = await GetAsync("/api/symbols", "application/xml");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var root = await XmlAsync(response);
        root.Name.ShouldBe(Schema + "symbols");
        root.Elements().Select(e => e.Name).ShouldAllBe(n => n == Schema + "symbol");
        root.Elements().Select(e => e.Element(Schema + "symbol")!.Value).ShouldBe(["BTCUSDT", "ADAUSDT", "ETHUSDT"]);
        root.Elements().First().Elements().Select(e => e.Name.LocalName)
            .ShouldBe(["symbol", "isActive", "latestPrice", "change24hPercent", "backfillStatus"]);
    }

    // ---- candles ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Candles_ReturnsOldestFirst()
    {
        await WriteCandlesAsync(
            Btc,
            Kline(At(12, 33), open: 15m, high: 16m, low: 14.5m, close: 15.5m, volume: 4m),
            Kline(At(12, 20), open: 1m, high: 1m, low: 1m, close: 1m, volume: 1m),
            Kline(At(12, 26), open: 10m, high: 12m, low: 9m, close: 11m, volume: 1.5m),
            Kline(At(12, 31), open: 13m, high: 17m, low: 12.5m, close: 14m, volume: 0.25m),
            Kline(At(12, 28), open: 11m, high: 13m, low: 10.5m, close: 12m, volume: 2m));

        using var response = await GetAsync("/api/btcusdt/candles?p=5m&limit=2");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var json = await JsonAsync(response);
        var rows = json.RootElement.EnumerateArray().ToList();

        // The latest candle (12:33) is in the 12:30 period, so limit = 2 covers 12:25 and 12:30; 12:20 is left out.
        // 12:25 = 12:26 + 12:28: open 10 (first), high 13, low 9, close 12 (last), volume 3.5.
        // 12:30 = 12:31 + 12:33: open 13, high 17, low 12.5, close 15.5, volume 4.25.
        rows.Count.ShouldBe(2);
        Names(rows[0]).ShouldBe(["bucketStart", "open", "high", "low", "close", "volume"]);
        Candle(rows[0]).ShouldBe(("2026-10-07T12:25:00Z", 10m, 13m, 9m, 12m, 3.5m));
        Candle(rows[1]).ShouldBe(("2026-10-07T12:30:00Z", 13m, 17m, 12.5m, 15.5m, 4.25m));
    }

    [Fact]
    public async Task Candles_DefaultLimit_ReturnsEveryPeriodUpTo300()
    {
        await WriteCandlesAsync(Btc, Candle(At(12, 0), 1m), Candle(At(12, 1), 2m), Candle(At(7, 2), 3m));

        using var response = await GetAsync("/api/BTCUSDT/candles?p=1m");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var json = await JsonAsync(response);

        // The 300 one-minute periods ending with 12:01 start at 07:02, so all three candles are inside.
        json.RootElement.EnumerateArray().Select(r => r.GetProperty("close").GetDecimal()).ShouldBe([3m, 1m, 2m]);
    }

    [Fact]
    public async Task Candles_Xml_IsAListOfCandleElements()
    {
        await WriteCandlesAsync(Btc, Candle(At(12, 0), 1.5m));

        using var response = await GetAsync("/api/BTCUSDT/candles?p=1m&limit=1", "application/xml");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var root = await XmlAsync(response);
        root.Name.ShouldBe(Schema + "candles");
        var candle = root.Elements().ShouldHaveSingleItem();
        candle.Name.ShouldBe(Schema + "candle");
        candle.Elements().Select(e => e.Name.LocalName).ShouldBe(["bucketStart", "open", "high", "low", "close", "volume"]);
        candle.Element(Schema + "close")!.Value.ShouldBe("1.50000000");
    }

    [Theory]
    [InlineData("p=1m&limit=0", "limit", "limit must be between 1 and 1000.")]
    [InlineData("p=1m&limit=1001", "limit", "limit must be between 1 and 1000.")]
    [InlineData("p=2h", "p", "p must be one of 1w, 1d, 30m, 5m, 1m.")]
    [InlineData("limit=5", "p", "p is required.")]
    public async Task Candles_InvalidParameters_Return400(string query, string parameter, string detail)
    {
        using var response = await GetAsync($"/api/BTCUSDT/candles?{query}");

        await ShouldBeValidationAsync(response, parameter, detail);
    }

    [Theory]
    [InlineData("NOPEUSDT", "unknown-symbol")]
    [InlineData("ETHUSDT", "no-data")]
    public async Task Candles_UnknownSymbolOrNoRows_Returns404(string symbol, string slug)
    {
        using var response = await GetAsync($"/api/{symbol}/candles?p=1m");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ReadAsync(response)).Type.ShouldBe(TypeBase + slug);
    }

    // ---- helpers ----------------------------------------------------------------------------------------------

    /// <summary>Six ticks around the 24 h window; four of them are inside (see <see cref="Avg_ReturnsExactAverageAndCoverage"/>).</summary>
    private Task SeedAverageTicksAsync(int symbolId) =>
        WriteTicksAsync(
            Tick(symbolId, 1, 999m, Now.AddHours(-24).AddMilliseconds(-1)),
            Tick(symbolId, 2, 1_234_567_890_123.10000001m, Now.AddHours(-24)),
            Tick(symbolId, 3, 1_234_567_890_123.20000002m, Now.AddHours(-12).AddMinutes(-30).AddSeconds(-15)),
            Tick(symbolId, 4, 1_234_567_890_123.30000003m, Now.AddHours(-1)),
            Tick(symbolId, 5, 1_234_567_890_123.40000004m, Now.AddSeconds(-30)),
            Tick(symbolId, 6, 1m, Now.AddSeconds(1)));

    /// <summary>1m candles of BTCUSDT on 2026-10-07 for the 5m SMA tests.</summary>
    private Task SeedFiveMinuteCandlesAsync() =>
        WriteCandlesAsync(
            Btc,
            Candle(At(12, 10), 50m),
            Candle(At(12, 15), 10m),
            Candle(At(12, 19), 11m),
            Candle(At(12, 20), 20m),
            Candle(At(12, 25), 30m),
            Candle(At(12, 29), 32.5m),
            Candle(At(12, 30), 1_000m),
            Candle(At(12, 34), 2_000m));

    /// <summary>1m candles of BTCUSDT across 2026-10-01..05 for the 1d SMA tests.</summary>
    private Task SeedDailyCandlesAsync() =>
        WriteCandlesAsync(
            Btc,
            Candle(Day(1, 0, 0), 80m),
            Candle(Day(1, 23, 59), 90m),
            Candle(Day(2, 0, 0), 100m),
            Candle(Day(2, 23, 59), 102m),
            Candle(Day(3, 12, 0), 104m),
            Candle(Day(4, 6, 0), 107m),
            Candle(Day(5, 0, 0), 200m));

    /// <summary>n = 2 daily buckets from 2026-10-01T00:00Z: closes 90 (10-01 23:59) and 102 (10-02 23:59) → 96.</summary>
    private static async Task ShouldBeTwoDaysFromOctoberFirstAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var json = await JsonAsync(response);
        var root = json.RootElement;
        root.GetProperty("startParameter").GetString().ShouldBe("2026-10-01T00:00:00Z");
        root.GetProperty("windowStart").GetString().ShouldBe("2026-10-01T00:00:00Z");
        root.GetProperty("windowEnd").GetString().ShouldBe("2026-10-03T00:00:00Z");
        root.GetProperty("value").GetDecimal().ShouldBe(96m);
        DataPoints(root).ShouldBe([("2026-10-01T00:00:00Z", 90m), ("2026-10-02T00:00:00Z", 102m)]);
    }

    /// <summary>A 400 <c>validation</c> problem about one parameter: its message is the detail and its only <c>errors</c> entry.</summary>
    private static async Task ShouldBeValidationAsync(HttpResponseMessage response, string parameter, string detail, string? accept = null)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        MediaType(response).ShouldBe(accept is null ? JsonMediaType : XmlMediaType);
        var problem = await ReadAsync(response);
        problem.Type.ShouldBe(TypeBase + "validation");
        problem.Status.ShouldBe(400);
        problem.Detail.ShouldBe(detail);
        problem.TraceId.ShouldNotBeNullOrWhiteSpace();
        var errors = Errors(problem);
        errors.Keys.ShouldBe([parameter]);
        errors[parameter].ShouldBe([detail]);
    }

    /// <summary>The <c>errors</c> member of a validation problem, from JSON or XML (one element per message).</summary>
    private static Dictionary<string, string[]> Errors(Problem problem)
    {
        if (problem.Raw.TrimStart().StartsWith('<'))
        {
            XNamespace rfc7807 = "urn:ietf:rfc:7807";
            return XDocument.Parse(problem.Raw).Root!.Element(rfc7807 + "errors")!.Elements()
                .GroupBy(e => e.Name.LocalName)
                .ToDictionary(g => g.Key, g => g.Select(e => e.Value).ToArray());
        }

        using var json = JsonDocument.Parse(problem.Raw);
        return json.RootElement.GetProperty("errors").EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.EnumerateArray().Select(v => v.GetString()!).ToArray());
    }

    private async Task<HttpResponseMessage> GetAsync(string path, string? accept = null)
    {
        using var client = api.CreateClient();
        using var request = Get(path, accept);
        return await client.SendAsync(request, Ct);
    }

    private async Task WriteTicksAsync(params Tick[] ticks)
    {
        var stored = await api.Services.GetRequiredService<ITickWriter>().WriteBatchAsync(ticks, Ct);
        stored.ShouldBe(ticks.Length);
    }

    private async Task WriteCandlesAsync(int symbolId, params Kline[] klines)
    {
        var stored = await api.Services.GetRequiredService<ICandleWriter>().InsertBackfillAsync(symbolId, klines, Ct);
        stored.ShouldBe(klines.Length);
    }

    private async Task DeactivateAsync(string symbol)
    {
        SymbolName.TryCreate(symbol, out var name).ShouldBeTrue();
        (await api.Services.GetRequiredService<ISymbolRepository>().DeactivateAsync(name, Ct)).ShouldBeTrue();
    }

    private int SymbolId(string name) => api.Database.InitialSymbols.Single(s => s.Name == name).Id;

    private static async Task<JsonDocument> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));

    private static async Task<XElement> XmlAsync(HttpResponseMessage response) =>
        XDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).Root!;

    private static string[] Names(JsonElement element) => [.. element.EnumerateObject().Select(p => p.Name)];

    private static string Scalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString()!,
        _ => value.GetRawText(),
    };

    private static List<(string BucketStart, decimal Close)> DataPoints(JsonElement root) =>
    [
        .. root.GetProperty("dataPoints").EnumerateArray()
            .Select(p => (p.GetProperty("bucketStart").GetString()!, p.GetProperty("close").GetDecimal())),
    ];

    private static (string, decimal, decimal, decimal, decimal, decimal) Candle(JsonElement row) =>
    (
        row.GetProperty("bucketStart").GetString()!,
        row.GetProperty("open").GetDecimal(),
        row.GetProperty("high").GetDecimal(),
        row.GetProperty("low").GetDecimal(),
        row.GetProperty("close").GetDecimal(),
        row.GetProperty("volume").GetDecimal()
    );

    private static Tick Tick(int symbolId, long id, decimal price, DateTime time) => new(symbolId, id, price, 1m, time, time);

    private static Kline Candle(DateTime openTime, decimal close) => Kline(openTime, close, close, close, close, 1m);

    private static Kline Kline(DateTime openTime, decimal open, decimal high, decimal low, decimal close, decimal volume) =>
        new(openTime, open, high, low, close, volume, volume * close);

    private static DateTime At(int hour, int minute) => new(2026, 10, 7, hour, minute, 0, DateTimeKind.Utc);

    private static DateTime Day(int day, int hour, int minute) => new(2026, 10, day, hour, minute, 0, DateTimeKind.Utc);
}
