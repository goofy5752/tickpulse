using System.Net;
using System.Text.Json;
using TickPulse.Cli.Tests.Support;

namespace TickPulse.Cli.Tests;

public sealed class CliTests
{
    private const string Json = "application/json; charset=utf-8";
    private const string Xml = "application/xml; charset=utf-8";
    private const string ProblemJson = "application/problem+json";
    private const string ProblemXml = "application/problem+xml; charset=utf-8";
    private const string SecretKey = "s3cret-key-9f2c";

    private const string AverageJson = """
        {"symbol":"BTCUSDT","averagePrice":67234.51234567,"from":"2026-11-03T10:00:05Z","to":"2026-11-04T09:59:58Z",
         "coveredSeconds":86393,"sampleCount":1234567,"windowHours":24}
        """;

    private const string AverageXml =
        "<averagePrice xmlns=\"https://tickpulse.dev/schema/v1\" xmlns:i=\"http://www.w3.org/2001/XMLSchema-instance\">"
        + "<symbol>BTCUSDT</symbol><averagePrice>67234.51234567</averagePrice><from>2026-11-03T10:00:05Z</from>"
        + "<to>2026-11-04T09:59:58Z</to><coveredSeconds>86393</coveredSeconds><sampleCount>1234567</sampleCount>"
        + "<windowHours>24</windowHours></averagePrice>";

    private const string SmaJson = """
        {"symbol":"BTCUSDT","period":"1d","requestedDataPoints":20,"actualDataPoints":3,"value":0.00001235,
         "windowStart":"2026-10-01T00:00:00Z","windowEnd":"2026-10-21T00:00:00Z","referenceTime":"2026-11-04T10:00:00Z",
         "startParameter":"2026-10-01T00:00:00Z","dataPoints":[
           {"bucketStart":"2026-10-01T00:00:00Z","close":0.00001234},
           {"bucketStart":"2026-10-02T00:00:00Z","close":0.00001201},
           {"bucketStart":"2026-10-03T00:00:00Z","close":0.0000127}]}
        """;

    private const string SymbolsJson = """
        [{"symbol":"BTCUSDT","isActive":true,"latestPrice":67234.5,"change24hPercent":1.25,"backfillStatus":"Completed"},
         {"symbol":"ADAUSDT","isActive":false,"latestPrice":0.3512,"change24hPercent":-2.5,"backfillStatus":"NotRequested"}]
        """;

    private const string AdminSymbolJson = """
        {"symbol":"BNBUSDT","isActive":true,"createdAt":"2026-11-04T10:00:00Z","deactivatedAt":null,
         "backfillStatus":"Pending","backfilledFrom":null}
        """;

    private const string UnknownSymbolProblem = """
        {"type":"https://tickpulse.dev/problems/unknown-symbol","title":"Unknown symbol","status":404,
         "detail":"XRPUSDT is not monitored.","traceId":"00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01"}
        """;

    [Fact]
    public async Task Avg_Table_PrintsAverageAndCoverage()
    {
        using var cli = new CliHarness(StubHttpHandler.Returns(HttpStatusCode.OK, Json, AverageJson));

        var exit = await cli.RunAsync("avg", "btcusdt");

        exit.ShouldBe(0);
        cli.Http.Last.Method.ShouldBe(HttpMethod.Get);
        cli.Http.Last.Uri.ShouldBe(new Uri("https://api.test/api/BTCUSDT/24hAvgPrice"));
        cli.Http.Last.Header("Accept").ShouldBe("application/json");
        var output = cli.Out.Output;
        output.ShouldContain("BTCUSDT");
        output.ShouldContain("67,234.51234567");
        output.ShouldContain("1,234,567");
        output.ShouldContain("Coverage");
        output.ShouldContain("23h 59m of 24h");
        output.ShouldContain("99.99%");
    }

    [Fact]
    public async Task Avg_FormatXml_SendsAcceptXml_PrintsRaw()
    {
        using var cli = new CliHarness(StubHttpHandler.Returns(HttpStatusCode.OK, Xml, AverageXml));

        var exit = await cli.RunAsync("avg", "BTCUSDT", "--format", "xml");

        exit.ShouldBe(0);
        cli.Http.Last.Header("Accept").ShouldBe("application/xml");
        cli.Out.Output.ShouldContain("<averagePrice>67234.51234567</averagePrice>");
        cli.Out.Output.ShouldContain("<sampleCount>1234567</sampleCount>");
        cli.Out.Output.ShouldNotContain("Coverage");
    }

    [Fact]
    public async Task Avg_FormatJson_SendsAcceptJson_PrintsRawValuesExactly()
    {
        using var cli = new CliHarness(StubHttpHandler.Returns(HttpStatusCode.OK, Json, AverageJson));

        var exit = await cli.RunAsync("avg", "BTCUSDT", "-f", "json");

        exit.ShouldBe(0);
        cli.Http.Last.Header("Accept").ShouldBe("application/json");
        cli.Out.Output.ShouldContain("67234.51234567");
        cli.Out.Output.ShouldContain("\"sampleCount\"");
    }

    [Fact]
    public async Task Sma_PassesQueryParamsExactly()
    {
        using var cli = new CliHarness(StubHttpHandler.Returns(HttpStatusCode.OK, Json, SmaJson));

        var exit = await cli.RunAsync("sma", "btcusdt", "-n", "20", "-p", "1d", "-s", "2026-10-01T00:00:00Z");

        exit.ShouldBe(0);
        cli.Http.Last.Uri.AbsolutePath.ShouldBe("/api/BTCUSDT/SimpleMovingAverage");
        cli.Http.Last.Uri.Query.ShouldBe("?n=20&p=1d&s=2026-10-01T00:00:00Z");
    }

    [Fact]
    public async Task Sma_StartWithOffset_IsSentAsUtc()
    {
        using var cli = new CliHarness(StubHttpHandler.Returns(HttpStatusCode.OK, Json, SmaJson));

        var exit = await cli.RunAsync("sma", "BTCUSDT", "--points", "5", "--period", "30m", "--start", "2026-10-01T03:30:00+03:00");

        exit.ShouldBe(0);
        cli.Http.Last.Uri.Query.ShouldBe("?n=5&p=30m&s=2026-10-01T00:30:00Z");
    }

    [Fact]
    public async Task Sma_WithoutStart_OmitsS()
    {
        using var cli = new CliHarness(StubHttpHandler.Returns(HttpStatusCode.OK, Json, SmaJson));

        (await cli.RunAsync("sma", "BTCUSDT", "-n", "20", "-p", "1w")).ShouldBe(0);

        cli.Http.Last.Uri.Query.ShouldBe("?n=20&p=1w");
    }

    [Fact]
    public async Task Sma_Table_PrintsValuePointsAndChart_WithTinyPricesPrecise()
    {
        using var cli = new CliHarness(StubHttpHandler.Returns(HttpStatusCode.OK, Json, SmaJson));

        (await cli.RunAsync("sma", "BTCUSDT", "-n", "20", "-p", "1d")).ShouldBe(0);

        var output = cli.Out.Output;
        output.ShouldContain("0.00001235");
        output.ShouldContain("0.00001234");
        output.ShouldContain("0.0000127");
        output.ShouldContain("2026-10-02");
        output.ShouldContain("3 of 20");
        output.ShouldContain("Closes");
        output.ShouldContain("█");
    }

    [Fact]
    public async Task ApiProblem_PrintsDetail_Exit1()
    {
        using var cli = new CliHarness(StubHttpHandler.Returns(HttpStatusCode.NotFound, ProblemJson, UnknownSymbolProblem));

        var exit = await cli.RunAsync("avg", "XRPUSDT");

        exit.ShouldBe(1);
        cli.Error.Output.ShouldContain("Unknown symbol");
        cli.Error.Output.ShouldContain("XRPUSDT is not monitored.");
        cli.Error.Output.ShouldContain("00-4bf92f3577b34da6a3ce929d0e0e4736");
    }

    [Fact]
    public async Task ValidationProblem_PrintsParameterErrors_Exit1()
    {
        const string problem = """
            {"type":"https://tickpulse.dev/problems/validation","title":"Validation failed","status":400,
             "detail":"s must not be in the future.","errors":{"s":["s must not be in the future."]},"traceId":"t-1"}
            """;
        using var cli = new CliHarness(StubHttpHandler.Returns(HttpStatusCode.BadRequest, ProblemJson, problem));

        var exit = await cli.RunAsync("sma", "BTCUSDT", "-n", "5", "-p", "1m", "-s", "2030-01-01");

        exit.ShouldBe(1);
        cli.Error.Output.ShouldContain("Validation failed");
        cli.Error.Output.ShouldContain("s: s must not be in the future.");
    }

    [Fact]
    public async Task XmlProblem_PrintsRawXmlAndExtractsDetail_Exit1()
    {
        const string problem =
            "<problem xmlns=\"urn:ietf:rfc:7807\"><status>404</status><title>Unknown symbol</title>"
            + "<type>https://tickpulse.dev/problems/unknown-symbol</type><detail>XRPUSDT is not monitored.</detail>"
            + "<traceId>t-2</traceId></problem>";
        using var cli = new CliHarness(StubHttpHandler.Returns(HttpStatusCode.NotFound, ProblemXml, problem));

        var exit = await cli.RunAsync("avg", "XRPUSDT", "--format", "xml");

        exit.ShouldBe(1);
        cli.Http.Last.Header("Accept").ShouldBe("application/xml");
        cli.Out.Output.ShouldContain("<detail>XRPUSDT is not monitored.</detail>");
        cli.Error.Output.ShouldContain("XRPUSDT is not monitored.");
    }

    [Fact]
    public async Task ServerError_Exit2()
    {
        const string problem = """
            {"type":"https://tickpulse.dev/problems/internal","title":"Internal server error","status":500,
             "detail":"An unexpected error occurred.","traceId":"t-3"}
            """;
        using var cli = new CliHarness(StubHttpHandler.Returns(HttpStatusCode.InternalServerError, ProblemJson, problem));

        (await cli.RunAsync("avg", "BTCUSDT")).ShouldBe(2);

        cli.Error.Output.ShouldContain("An unexpected error occurred.");
    }

    [Fact]
    public async Task NetworkError_Exit2()
    {
        using var cli = new CliHarness(StubHttpHandler.Throws(new HttpRequestException("No such host is known.")));

        var exit = await cli.RunAsync("avg", "BTCUSDT");

        exit.ShouldBe(2);
        cli.Error.Output.ShouldContain("https://api.test");
        cli.Error.Output.ShouldContain("No such host is known.");
    }

    [Fact]
    public async Task ApiUrlOption_OverridesEnvironment()
    {
        using var cli = new CliHarness(StubHttpHandler.Returns(HttpStatusCode.OK, Json, AverageJson));

        (await cli.RunAsync("avg", "BTCUSDT", "--api-url", "http://localhost:5080/")).ShouldBe(0);

        cli.Http.Last.Uri.ShouldBe(new Uri("http://localhost:5080/api/BTCUSDT/24hAvgPrice"));
    }

    [Fact]
    public async Task SymbolsAdd_SendsApiKeyHeader()
    {
        using var cli = new CliHarness(StubHttpHandler.Returns(HttpStatusCode.Created, Json, AdminSymbolJson));

        var exit = await cli.RunAsync("symbols", "add", "bnbusdt", "--api-key", "s3cret-key");

        exit.ShouldBe(0);
        var request = cli.Http.Last;
        request.Method.ShouldBe(HttpMethod.Post);
        request.Uri.AbsolutePath.ShouldBe("/api/admin/symbols");
        request.Header("X-Api-Key").ShouldBe("s3cret-key");
        using var body = JsonDocument.Parse(request.Body!);
        body.RootElement.GetProperty("symbol").GetString().ShouldBe("BNBUSDT");
        cli.Out.Output.ShouldContain("BNBUSDT added");
        cli.Out.Output.ShouldNotContain("s3cret-key");
        cli.Error.Output.ShouldNotContain("s3cret-key");
    }

    [Fact]
    public async Task SymbolsAdd_KeyFromEnvironment_AlreadyActive_ReportsIt()
    {
        using var cli = new CliHarness(StubHttpHandler.Returns(HttpStatusCode.OK, Json, AdminSymbolJson));
        cli.Variables[CliEnvironment.ApiKeyVariable] = "env-key";

        var exit = await cli.RunAsync("symbols", "add", "BNBUSDT");

        exit.ShouldBe(0);
        cli.Http.Last.Header("X-Api-Key").ShouldBe("env-key");
        cli.Out.Output.ShouldContain("already active");
    }

    [Fact]
    public async Task SymbolsAdd_WithoutKey_Exit64_SendsNothing()
    {
        using var cli = new CliHarness(StubHttpHandler.Returns(HttpStatusCode.Created, Json, AdminSymbolJson));

        (await cli.RunAsync("symbols", "add", "BNBUSDT")).ShouldBe(64);

        cli.Http.Requests.ShouldBeEmpty();
        cli.Error.Output.ShouldContain("TICKPULSE_API_KEY");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "unauthorized", "rejected")]
    [InlineData(HttpStatusCode.Forbidden, "admin-disabled", "disabled")]
    [InlineData(HttpStatusCode.Conflict, "symbol-limit", "limit")]
    [InlineData(HttpStatusCode.UnprocessableEntity, "invalid-symbol", "spot")]
    public async Task SymbolsAdd_AdminProblems_FriendlyMessage_Exit1(HttpStatusCode status, string slug, string hint)
    {
        var problem = $$"""{"type":"https://tickpulse.dev/problems/{{slug}}","title":"T","status":{{(int)status}},"detail":"D"}""";
        using var cli = new CliHarness(StubHttpHandler.Returns(status, ProblemJson, problem));

        (await cli.RunAsync("symbols", "add", "BNBUSDT", "--api-key", SecretKey)).ShouldBe(1);

        cli.Error.Output.ShouldContain(hint, Case.Insensitive);
        cli.Error.Output.ShouldNotContain(SecretKey);
        cli.Out.Output.ShouldNotContain(SecretKey);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "rejected")]
    [InlineData(HttpStatusCode.Forbidden, "disabled")]
    [InlineData(HttpStatusCode.Conflict, "limit")]
    [InlineData(HttpStatusCode.UnprocessableEntity, "spot")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "try again")]
    public async Task SymbolsAdd_ProblemWithoutBody_HintFromStatus(HttpStatusCode status, string hint)
    {
        using var cli = new CliHarness(new StubHttpHandler(_ => StubHttpHandler.Empty(status)));

        (await cli.RunAsync("symbols", "add", "BNBUSDT", "--api-key", SecretKey)).ShouldBe(ExitCodesFor(status));

        cli.Error.Output.ShouldContain(hint, Case.Insensitive);
    }

    [Fact]
    public async Task SymbolsAdd_UpstreamUnavailable_Exit2()
    {
        const string problem = """{"type":"https://tickpulse.dev/problems/upstream-unavailable","title":"Upstream unavailable","status":503,"detail":"D"}""";
        using var cli = new CliHarness(StubHttpHandler.Returns(HttpStatusCode.ServiceUnavailable, ProblemJson, problem));

        (await cli.RunAsync("symbols", "add", "BNBUSDT", "--api-key", "k")).ShouldBe(2);

        cli.Error.Output.ShouldContain("Binance", Case.Insensitive);
    }

    [Fact]
    public async Task SymbolsRemove_SendsDelete_Exit0()
    {
        using var cli = new CliHarness(new StubHttpHandler(_ => StubHttpHandler.Empty(HttpStatusCode.NoContent)));

        (await cli.RunAsync("symbols", "remove", "bnbusdt", "--api-key", "k")).ShouldBe(0);

        cli.Http.Last.Method.ShouldBe(HttpMethod.Delete);
        cli.Http.Last.Uri.AbsolutePath.ShouldBe("/api/admin/symbols/BNBUSDT");
        cli.Http.Last.Header("X-Api-Key").ShouldBe("k");
        cli.Out.Output.ShouldContain("BNBUSDT removed");
    }

    [Fact]
    public async Task SymbolsList_WithoutKey_UsesPublicList()
    {
        using var cli = new CliHarness(StubHttpHandler.Returns(HttpStatusCode.OK, Json, SymbolsJson));

        (await cli.RunAsync("symbols", "list")).ShouldBe(0);

        cli.Http.Last.Uri.AbsolutePath.ShouldBe("/api/symbols");
        cli.Http.Last.Header("X-Api-Key").ShouldBeNull();
        cli.Out.Output.ShouldContain("BTCUSDT");
        cli.Out.Output.ShouldContain("67,234.50");
        cli.Out.Output.ShouldContain("+1.25%");
        cli.Out.Output.ShouldContain("-2.50%");
    }

    [Fact]
    public async Task SymbolsList_WithKey_UsesAdminList()
    {
        var list = "[" + AdminSymbolJson + "]";
        using var cli = new CliHarness(StubHttpHandler.Returns(HttpStatusCode.OK, Json, list));

        (await cli.RunAsync("symbols", "list", "--api-key", "k")).ShouldBe(0);

        cli.Http.Last.Uri.AbsolutePath.ShouldBe("/api/admin/symbols");
        cli.Http.Last.Header("X-Api-Key").ShouldBe("k");
        cli.Out.Output.ShouldContain("BNBUSDT");
        cli.Out.Output.ShouldContain("Pending");
    }

    [Fact]
    public async Task InvalidPeriod_Exit64()
    {
        using var cli = new CliHarness(StubHttpHandler.Returns(HttpStatusCode.OK, Json, SmaJson));

        var exit = await cli.RunAsync("sma", "BTCUSDT", "-n", "20", "-p", "7m");

        exit.ShouldBe(64);
        cli.Http.Requests.ShouldBeEmpty();
        cli.Error.Output.ShouldContain("7m");
    }

    [Theory]
    [InlineData("sma", "BTCUSDT", "-n", "0", "-p", "1d")]
    [InlineData("sma", "BTCUSDT", "-p", "1d")]
    [InlineData("sma", "BTCUSDT", "-n", "abc", "-p", "1d")]
    [InlineData("sma", "BTCUSDT", "-n", "1001", "-p", "1d")]
    [InlineData("watch", "BTCUSDT", "--interval", "2h")]
    [InlineData("sma", "BTCUSDT", "-n", "5", "-p", "1d", "-s", "yesterday")]
    [InlineData("avg", "BTC/../admin")]
    [InlineData("avg", "BTCUSDT", "--format", "yaml")]
    [InlineData("avg", "BTCUSDT", "--api-url", "not a url")]
    [InlineData("watch", "BTCUSDT", "--interval", "fast")]
    [InlineData("frobnicate")]
    public async Task UsageErrors_Exit64(params string[] args)
    {
        using var cli = new CliHarness(StubHttpHandler.Returns(HttpStatusCode.OK, Json, AverageJson));

        (await cli.RunAsync(args)).ShouldBe(64);

        cli.Http.Requests.ShouldBeEmpty();
        cli.Error.Output.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Watch_PollsSymbols_StopsCleanlyOnCancel_Exit0()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var cli = new CliHarness(StubHttpHandler.Returns(HttpStatusCode.OK, Json, SymbolsJson));
        cli.StopAfter(2, cts);

        var exit = await cli.RunAsync(cts.Token, "watch", "btcusdt", "--interval", "1h");

        exit.ShouldBe(0);
        cli.Http.Requests.Count.ShouldBe(2);
        cli.Http.Requests.ShouldAllBe(r => r.Uri.AbsolutePath == "/api/symbols");
        cli.Out.Output.ShouldContain("67,234.50");
    }

    [Fact]
    public async Task Watch_WithSma_AlsoPollsSma()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var cli = new CliHarness(new StubHttpHandler(request =>
            request.RequestUri!.AbsolutePath == "/api/symbols"
                ? StubHttpHandler.Response(HttpStatusCode.OK, Json, SymbolsJson)
                : StubHttpHandler.Response(HttpStatusCode.OK, Json, SmaJson)));
        // Stop during the second poll, after the first one (symbols + SMA) has been drawn. The 1h interval would
        // hang on a real clock; the fake one is advanced by every response.
        cli.StopAfter(3, cts);

        (await cli.RunAsync(cts.Token, "watch", "BTCUSDT", "-n", "20", "-p", "1d", "-i", "1h")).ShouldBe(0);

        cli.Http.Requests.Take(3).Select(r => r.Uri.PathAndQuery)
            .ShouldBe(["/api/symbols", "/api/BTCUSDT/SimpleMovingAverage?n=20&p=1d", "/api/symbols"]);
        cli.Out.Output.ShouldContain("SMA(20 × 1d)");
        cli.Out.Output.ShouldContain("0.00001235");
    }

    [Fact]
    public async Task Watch_OutputRedirected_PrintsOneLinePerPoll()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var cli = new CliHarness(StubHttpHandler.Returns(HttpStatusCode.OK, Json, SymbolsJson), outputRedirected: true);
        cli.StopAfter(3, cts);

        (await cli.RunAsync(cts.Token, "watch", "BTCUSDT", "-i", "1h")).ShouldBe(0);

        var lines = cli.Out.Lines.Where(l => l.Contains("BTCUSDT", StringComparison.Ordinal)).ToList();
        lines.Count.ShouldBeGreaterThanOrEqualTo(2);
        lines.ShouldAllBe(l => l.Contains("67,234.50") && l.Contains("+1.25%"));
        cli.Out.Output.ShouldNotContain("╭");
    }

    [Fact]
    public async Task Watch_UnknownSymbol_Exit1()
    {
        using var cli = new CliHarness(StubHttpHandler.Returns(HttpStatusCode.OK, Json, SymbolsJson));

        (await cli.RunAsync("watch", "XRPUSDT")).ShouldBe(1);

        cli.Error.Output.ShouldContain("XRPUSDT");
    }

    [Fact]
    public async Task NoArguments_PrintsWelcome_Exit0()
    {
        using var cli = new CliHarness(StubHttpHandler.Returns(HttpStatusCode.OK, Json, AverageJson));

        (await cli.RunAsync()).ShouldBe(0);

        cli.Out.Output.ShouldContain("tickpulse avg BTCUSDT");
        cli.Http.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("avg", "BTCUSDT", "--api-key", SecretKey, "bogus")]
    [InlineData("avg", "BTCUSDT", "bogus", "-k", SecretKey)]
    [InlineData("frobnicate", "--api-key=" + SecretKey)]
    [InlineData("sma", "BTCUSDT", "-n", SecretKey, "-p", "1d", "--api-key", SecretKey)]
    [InlineData("sma", "BTCUSDT", "-n", SecretKey, "-p", "1d", "-k:" + SecretKey)]
    public async Task UsageError_NeverEchoesApiKey(params string[] args)
    {
        using var cli = new CliHarness(StubHttpHandler.Returns(HttpStatusCode.OK, Json, AverageJson));

        (await cli.RunAsync(args)).ShouldBe(64);

        cli.Error.Output.ShouldNotBeEmpty();
        cli.Error.Output.ShouldNotContain(SecretKey);
        cli.Out.Output.ShouldNotContain(SecretKey);
    }

    [Fact]
    public async Task ApiKey_OnAnalyticsCommand_IsAccepted_AndNotSent()
    {
        using var cli = new CliHarness(StubHttpHandler.Returns(HttpStatusCode.OK, Json, AverageJson));

        (await cli.RunAsync("avg", "BTCUSDT", "--api-key", SecretKey)).ShouldBe(0);

        cli.Http.Last.Header("X-Api-Key").ShouldBeNull();
        cli.Error.Output.ShouldNotContain(SecretKey);
    }

    [Fact]
    public async Task HttpClientTimeout_Exit2()
    {
        using var cli = new CliHarness(StubHttpHandler.Throws(new TaskCanceledException("timed out", new TimeoutException())));

        (await cli.RunAsync("avg", "BTCUSDT")).ShouldBe(2);

        cli.Error.Output.ShouldContain("No answer within");
    }

    [Fact]
    public async Task ProblemDetail_WithMarkup_RendersLiterally()
    {
        const string problem = """
            {"type":"https://tickpulse.dev/problems/no-data","title":"No [bold]data[/]","status":404,
             "detail":"Nothing for [red]BTCUSDT[/] yet.","traceId":"[t]"}
            """;
        using var cli = new CliHarness(StubHttpHandler.Returns(HttpStatusCode.NotFound, ProblemJson, problem));

        (await cli.RunAsync("avg", "BTCUSDT")).ShouldBe(1);

        cli.Error.Output.ShouldContain("Nothing for [red]BTCUSDT[/] yet.");
        cli.Error.Output.ShouldContain("No [bold]data[/]");
    }

    [Theory]
    [InlineData("table", "text/html", "<html><body>Welcome</body></html>")]
    [InlineData("json", "text/html", "<html><body>Welcome</body></html>")]
    [InlineData("json", Json, "{\"symbol\":")]
    [InlineData("xml", "text/html", "<html><body>Welcome")]
    [InlineData("xml", Json, "{\"symbol\":\"BTCUSDT\"}")]
    [InlineData("table", Json, "")]
    public async Task UnreadableSuccessBody_Exit2(string format, string contentType, string body)
    {
        using var cli = new CliHarness(StubHttpHandler.Returns(HttpStatusCode.OK, contentType, body));

        (await cli.RunAsync("avg", "BTCUSDT", "--format", format)).ShouldBe(2);

        cli.Error.Output.ShouldContain("api.test");
    }

    [Theory]
    [InlineData("json", Json)]
    [InlineData("xml", Xml)]
    public async Task RawOutput_Redirected_IsByteForByte(string format, string contentType)
    {
        var body = format == "json" ? AverageJson.ReplaceLineEndings(string.Empty) : AverageXml;
        using var cli = new CliHarness(StubHttpHandler.Returns(HttpStatusCode.OK, contentType, body), outputRedirected: true);

        (await cli.RunAsync("avg", "BTCUSDT", "-f", format)).ShouldBe(0);

        cli.Out.Output.ShouldBe(body);
    }

    [Theory]
    [InlineData("https://user:hunter2@api.test", "credentials")]
    [InlineData("http://user:hunter2@bad host", "absolute")]
    [InlineData("https://api.test/?region=eu", "query")]
    [InlineData("https://api.test/#top", "fragment")]
    [InlineData("ftp://api.test", "absolute")]
    public async Task ApiUrl_Rejected_WithoutEchoingCredentials(string url, string reason)
    {
        using var cli = new CliHarness(StubHttpHandler.Returns(HttpStatusCode.OK, Json, AverageJson));

        (await cli.RunAsync("avg", "BTCUSDT", "--api-url", url)).ShouldBe(64);

        cli.Http.Requests.ShouldBeEmpty();
        cli.Error.Output.ShouldContain(reason, Case.Insensitive);
        cli.Error.Output.ShouldNotContain("hunter2");
    }

    [Fact]
    public async Task ApiUrl_InvalidInEnvironment_Exit64()
    {
        using var cli = new CliHarness(StubHttpHandler.Returns(HttpStatusCode.OK, Json, AverageJson));
        cli.Variables[CliEnvironment.ApiUrlVariable] = "https://user:hunter2@api.test";

        (await cli.RunAsync("avg", "BTCUSDT")).ShouldBe(64);

        cli.Http.Requests.ShouldBeEmpty();
        cli.Error.Output.ShouldNotContain("hunter2");
    }

    [Fact]
    public async Task RootHelp_ListsExitCodes()
    {
        using var cli = new CliHarness(StubHttpHandler.Returns(HttpStatusCode.OK, Json, AverageJson));

        (await cli.RunAsync("--help")).ShouldBe(0);

        var help = cli.Help.ToString();
        help.ShouldContain("Usage:");
        help.ShouldContain("tickpulse [command] [options]");
        foreach (var code in new[] { "0 ", "1 ", "2 ", "64 ", "70 ", "130 " })
        {
            help.ShouldContain("  " + code);
        }
    }

    private static int ExitCodesFor(HttpStatusCode status) => (int)status >= 500 ? 2 : 1;
}
