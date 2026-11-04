using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TickPulse.Application.Abstractions;
using TickPulse.Domain;
using TickPulse.Integration.Tests.Fixtures;
using TickPulse.Integration.Tests.Ingestion;
using static TickPulse.Integration.Tests.Api.ProblemReader;

namespace TickPulse.Integration.Tests.Api;

/// <summary>
/// The admin symbol routes end to end: <c>X-Api-Key</c> authentication, the <c>Admin</c> policy, the add/remove rules
/// against TimescaleDB and the fake exchange, and the negotiated problems.
/// </summary>
public sealed class AdminEndpointTests(ApiFactory api) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private const string Path = "/api/admin/symbols";
    private const string ApiKeyHeader = "X-Api-Key";

    private static readonly XNamespace Schema = "https://tickpulse.dev/schema/v1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await api.Database.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // ---- authentication ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData("GET", Path)]
    [InlineData("POST", Path)]
    [InlineData("DELETE", Path + "/BTCUSDT")]
    public async Task NoKey_Returns401Problem(string method, string path)
    {
        using var response = await SendAsync(api, new HttpMethod(method), path, key: null, body: method == "POST" ? "BNBUSDT" : null);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ShouldHaveSingleItem().Scheme.ShouldBe("ApiKey");
        MediaType(response).ShouldBe(JsonMediaType);
        var problem = await ReadAsync(response);
        problem.Type.ShouldBe(TypeBase + "unauthorized");
        problem.Status.ShouldBe(401);
        problem.Title.ShouldBe("Unauthorized");
        problem.Detail.ShouldNotBeNull().ShouldContain(ApiKeyHeader);
        problem.TraceId.ShouldNotBeNullOrWhiteSpace();

        // Nothing was written.
        (await FindAsync(api, "BTCUSDT")).ShouldNotBeNull().IsActive.ShouldBeTrue();
    }

    [Theory]
    [InlineData("wrong-key")]
    [InlineData("test-key ")]
    [InlineData("TEST-KEY")]
    [InlineData("test-ke")]
    [InlineData("test-key-and-more")]
    public async Task WrongKey_Returns401Problem(string key)
    {
        using var response = await SendAsync(api, HttpMethod.Delete, Path + "/BTCUSDT", key);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ShouldHaveSingleItem().Scheme.ShouldBe("ApiKey");
        var problem = await ReadAsync(response);
        problem.Type.ShouldBe(TypeBase + "unauthorized");
        problem.Status.ShouldBe(401);
        problem.Raw.ShouldNotContain(key);
        (await FindAsync(api, "BTCUSDT")).ShouldNotBeNull().IsActive.ShouldBeTrue();
    }

    [Fact]
    public async Task TwoKeyHeaders_Returns401Problem()
    {
        using var client = api.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(Path, UriKind.Relative));
        request.Headers.Add(ApiKeyHeader, [ApiFactory.AdminApiKey, ApiFactory.AdminApiKey]);

        using var response = await client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task LowercaseHeaderName_IsAccepted()
    {
        using var client = api.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(Path, UriKind.Relative));
        request.Headers.TryAddWithoutValidation("x-api-key", ApiFactory.AdminApiKey);

        using var response = await client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task KeyInQueryString_Returns401()
    {
        using var response = await SendAsync(api, HttpMethod.Get, Path + "?X-Api-Key=" + ApiFactory.AdminApiKey, key: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ReadAsync(response)).Type.ShouldBe(TypeBase + "unauthorized");
    }

    [Fact]
    public async Task KeyInCookie_Returns401()
    {
        using var client = api.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(Path, UriKind.Relative));
        request.Headers.Add("Cookie", "X-Api-Key=" + ApiFactory.AdminApiKey);

        using var response = await client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ReadAsync(response)).Type.ShouldBe(TypeBase + "unauthorized");
    }

    [Fact]
    public void AdminRoutes_AllRequireTheAdminPolicy_AndNoneAllowAnonymous()
    {
        var endpoints = api.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith("api/admin", StringComparison.OrdinalIgnoreCase) == true)
            .ToList();

        endpoints.Select(e => e.DisplayName).ShouldNotBeEmpty();
        endpoints.Count.ShouldBe(3, "GET and POST api/admin/symbols, DELETE api/admin/symbols/{symbol}");
        foreach (var endpoint in endpoints)
        {
            endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().ShouldContain(a => a.Policy == "Admin", endpoint.DisplayName);
            endpoint.Metadata.GetMetadata<IAllowAnonymous>().ShouldBeNull(endpoint.DisplayName);
        }
    }

    [Fact]
    public async Task FailedAuth_LogsWarningWithHashedClient_NeverTheKey()
    {
        var logs = new IngestionTestHost.LogCapture();
        await using var logged = new ApiFactory(api.Database).WithLogging(logs);
        await logged.InitializeAsync();

        (await SendAsync(logged, HttpMethod.Get, Path, key: "guess-secret-123")).Dispose();
        (await SendAsync(logged, HttpMethod.Get, Path, key: null)).Dispose();

        var failures = logs.Entries.Where(e => e.EventName == "LogAuthenticationFailed").ToList();
        failures.Count.ShouldBe(2);
        failures.ShouldAllBe(e => e.Level == LogLevel.Warning);
        failures[0].Message.ShouldContain("wrong key");
        failures[1].Message.ShouldContain("missing key");
        failures.ShouldAllBe(e => e.Message.Contains("client ", StringComparison.Ordinal));
        logs.Entries.ShouldNotContain(e => e.Message.Contains("guess-secret-123", StringComparison.Ordinal));
        logs.Entries.ShouldNotContain(e => e.Message.Contains(ApiFactory.AdminApiKey, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("test-key", 1)]
    [InlineData("0123456789abcdef0123456789abcdef", 0)]
    public async Task ShortKey_LogsWarningAtStartup(string key, int warnings)
    {
        var logs = new IngestionTestHost.LogCapture();
        await using var host = new ApiFactory(api.Database).WithSetting("Admin:ApiKey", key).WithLogging(logs);
        await host.InitializeAsync();

        _ = host.Services; // starts the host

        var entries = logs.Entries.Where(e => e.EventName == "LogShortApiKey").ToList();
        entries.Count.ShouldBe(warnings);
        entries.ShouldAllBe(e => e.Level == LogLevel.Warning && !e.Message.Contains(key, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(" padded-key")]
    [InlineData("padded-key\t")]
    public async Task KeyWithSurroundingWhitespace_FailsStartup(string key)
    {
        await using var host = new ApiFactory(api.Database).WithSetting("Admin:ApiKey", key);
        await host.InitializeAsync();

        var ex = Should.Throw<Exception>(() => host.Services);

        Messages(ex).ShouldContain(m => m.Contains("Admin:ApiKey", StringComparison.Ordinal) && m.Contains("whitespace", StringComparison.Ordinal));
        Messages(ex).ShouldNotContain(m => m.Contains("padded-key", StringComparison.Ordinal));

        static IEnumerable<string> Messages(Exception? e)
        {
            for (; e is not null; e = e.InnerException)
            {
                yield return e.Message;
                if (e is AggregateException aggregate)
                {
                    foreach (var inner in aggregate.InnerExceptions.SelectMany(Messages))
                    {
                        yield return inner;
                    }
                }
            }
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task KeyNotConfigured_Returns403AdminDisabled(string configured)
    {
        await using var disabled = new ApiFactory(api.Database).WithSetting("Admin:ApiKey", configured);
        await disabled.InitializeAsync();

        foreach (var key in new[] { null, ApiFactory.AdminApiKey, configured })
        {
            using var response = await SendAsync(disabled, HttpMethod.Get, Path, key);

            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            response.Headers.WwwAuthenticate.ShouldBeEmpty();
            var problem = await ReadAsync(response);
            problem.Type.ShouldBe(TypeBase + "admin-disabled");
            problem.Title.ShouldBe("Admin API disabled");
            problem.Status.ShouldBe(403);
        }

        // The public API is unaffected.
        using var client = disabled.CreateClient();
        using var symbols = await client.GetAsync(new Uri("/api/symbols", UriKind.Relative), Ct);
        symbols.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // ---- add --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Add_Valid_Returns201_AndSubscribes()
    {
        // The reconcile timer is an hour away, so only the change signal can make the worker subscribe in time.
        await using var live = new ApiFactory(api.Database)
            .WithWorkers()
            .WithSystemClock()
            .WithSetting("Ingestion:ReconcileInterval", "01:00:00");
        await live.InitializeAsync();
        live.Binance.SetSymbolStatus("BNBUSDT", "TRADING");
        using var client = live.CreateClient();
        await live.Binance.WaitUntilAsync(b => b.SubscribedStreams.Contains("btcusdt@aggTrade"), cancellationToken: Ct);

        using var response = await SendAsync(live, HttpMethod.Post, Path, ApiFactory.AdminApiKey, body: " bnbusdt ");

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        MediaType(response).ShouldBe("application/json");
        using (var json = await JsonAsync(response))
        {
            var root = json.RootElement;
            root.GetProperty("symbol").GetString().ShouldBe("BNBUSDT");
            root.GetProperty("isActive").GetBoolean().ShouldBeTrue();
            root.GetProperty("backfillStatus").GetString().ShouldBe("NotRequested");
            root.GetProperty("createdAt").GetString().ShouldNotBeNull().ShouldEndWith("Z");
            root.GetProperty("deactivatedAt").ValueKind.ShouldBe(JsonValueKind.Null);
        }

        await live.Binance.WaitUntilAsync(
            b => b.SubscribedStreams.Contains("bnbusdt@aggTrade")
                && b.ReceivedCommands.Any(c => c.Contains("\"SUBSCRIBE\"", StringComparison.Ordinal) && c.Contains("bnbusdt@aggTrade", StringComparison.Ordinal)),
            cancellationToken: Ct);
        (await FindAsync(live, "BNBUSDT")).ShouldNotBeNull().IsActive.ShouldBeTrue();
    }

    [Fact]
    public async Task Add_AlreadyActive_Returns200()
    {
        var requestsBefore = api.Binance.ReceivedRequests.Count;

        using var response = await SendAsync(api, HttpMethod.Post, Path, ApiFactory.AdminApiKey, body: "btcusdt");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var json = await JsonAsync(response);
        json.RootElement.GetProperty("symbol").GetString().ShouldBe("BTCUSDT");
        json.RootElement.GetProperty("isActive").GetBoolean().ShouldBeTrue();

        // Answered from the database alone: the exchange was not asked.
        api.Binance.ReceivedRequests.Count.ShouldBe(requestsBefore);
    }

    [Fact]
    public async Task Add_Inactive_Reactivates_Returns201()
    {
        (await SendAsync(api, HttpMethod.Delete, Path + "/ADAUSDT", ApiFactory.AdminApiKey)).Dispose();

        using var response = await SendAsync(api, HttpMethod.Post, Path, ApiFactory.AdminApiKey, body: "ADAUSDT");

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        using var json = await JsonAsync(response);
        json.RootElement.GetProperty("isActive").GetBoolean().ShouldBeTrue();
        json.RootElement.GetProperty("deactivatedAt").GetString().ShouldNotBeNull().ShouldEndWith("Z"); // the last deactivation is kept
        (await FindAsync(api, "ADAUSDT")).ShouldNotBeNull().IsActive.ShouldBeTrue();
    }

    [Theory]
    [InlineData("BTC-USDT")]
    [InlineData("ABC")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Add_InvalidFormat_Returns422(string? symbol)
    {
        var requestsBefore = api.Binance.ReceivedRequests.Count;

        using var response = await SendAsync(api, HttpMethod.Post, Path, ApiFactory.AdminApiKey, body: symbol, bodyIsSymbol: true);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var problem = await ReadAsync(response);
        problem.Type.ShouldBe(TypeBase + "invalid-symbol");
        problem.Status.ShouldBe(422);
        api.Binance.ReceivedRequests.Count.ShouldBe(requestsBefore);
    }

    [Theory]
    [InlineData(null)]       // unknown to the exchange (-1121)
    [InlineData("BREAK")]    // listed but not trading
    public async Task Add_NotOnExchange_Returns422(string? status)
    {
        if (status is not null)
        {
            api.Binance.SetSymbolStatus("LUNAUSDT", status);
        }

        using var response = await SendAsync(api, HttpMethod.Post, Path, ApiFactory.AdminApiKey, body: "LUNAUSDT");

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var problem = await ReadAsync(response);
        problem.Type.ShouldBe(TypeBase + "invalid-symbol");
        problem.Detail.ShouldNotBeNull().ShouldContain("LUNAUSDT");
        (await FindAsync(api, "LUNAUSDT")).ShouldBeNull();
        api.Binance.RemoveSymbol("LUNAUSDT");
    }

    [Fact]
    public async Task Add_OverLimit_Returns409()
    {
        await using var limited = new ApiFactory(api.Database).WithSetting("Admin:MaxActiveSymbols", "3");
        await limited.InitializeAsync();
        limited.Binance.SetSymbolStatus("BNBUSDT", "TRADING");

        using var response = await SendAsync(limited, HttpMethod.Post, Path, ApiFactory.AdminApiKey, body: "BNBUSDT");

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var problem = await ReadAsync(response);
        problem.Type.ShouldBe(TypeBase + "symbol-limit");
        problem.Status.ShouldBe(409);
        (await FindAsync(limited, "BNBUSDT")).ShouldBeNull();

        // An already active symbol is still a 200 at the limit.
        using var again = await SendAsync(limited, HttpMethod.Post, Path, ApiFactory.AdminApiKey, body: "ETHUSDT");
        again.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Add_ExchangeUnavailable_Returns503()
    {
        await using var down = new ApiFactory(api.Database);
        await down.InitializeAsync();
        down.Binance.FailNextExchangeInfo(503, count: 100);

        using var response = await SendAsync(down, HttpMethod.Post, Path, ApiFactory.AdminApiKey, body: "BNBUSDT");

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        var problem = await ReadAsync(response);
        problem.Type.ShouldBe(TypeBase + "upstream-unavailable");
        problem.Title.ShouldBe("Upstream unavailable");
        problem.Status.ShouldBe(503);
        (await FindAsync(down, "BNBUSDT")).ShouldBeNull();
    }

    [Fact]
    public async Task Add_MissingBody_Returns400Validation()
    {
        using var client = api.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Path, UriKind.Relative))
        {
            Content = new StringContent(string.Empty, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(ApiKeyHeader, ApiFactory.AdminApiKey);

        using var response = await client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadAsync(response)).Type.ShouldBe(TypeBase + "validation");
    }

    [Fact]
    public async Task Add_XmlBody_IsAccepted()
    {
        api.Binance.SetSymbolStatus("XRPUSDT", "TRADING");
        using var client = api.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Path, UriKind.Relative))
        {
            Content = new StringContent(
                """<addSymbolRequest xmlns="https://tickpulse.dev/schema/v1"><symbol>xrpusdt</symbol></addSymbolRequest>""",
                Encoding.UTF8,
                "application/xml"),
        };
        request.Headers.Add(ApiKeyHeader, ApiFactory.AdminApiKey);
        request.Headers.Add("Accept", "application/xml");

        using var response = await client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        MediaType(response).ShouldBe("application/xml");
        var root = await XmlAsync(response);
        root.Name.ShouldBe(Schema + "adminSymbol");
        root.Element(Schema + "symbol")!.Value.ShouldBe("XRPUSDT");
        root.Element(Schema + "isActive")!.Value.ShouldBe("true");
    }

    // ---- remove -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Remove_Returns204_HistoryStillQueryable()
    {
        var ada = await FindAsync(api, "ADAUSDT");
        var now = api.Clock.GetUtcNow().UtcDateTime;
        var written = await api.Services.GetRequiredService<ITickWriter>().WriteBatchAsync(
            [new Tick(ada!.Id, 1, 0.35m, 10m, now.AddMinutes(-5), now.AddMinutes(-5))],
            Ct);
        written.ShouldBe(1);

        using var response = await SendAsync(api, HttpMethod.Delete, Path + "/adausdt", ApiFactory.AdminApiKey);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldBeEmpty();
        var stored = (await FindAsync(api, "ADAUSDT")).ShouldNotBeNull();
        stored.IsActive.ShouldBeFalse();
        stored.DeactivatedAtUtc.ShouldNotBeNull();

        using var client = api.CreateClient();
        using var average = await client.GetAsync(new Uri("/api/ADAUSDT/24hAvgPrice", UriKind.Relative), Ct);
        average.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var json = await JsonAsync(average);
        json.RootElement.GetProperty("averagePrice").GetDecimal().ShouldBe(0.35m);
    }

    [Theory]
    [InlineData("NOPEUSDT")]
    [InlineData("BTC-USDT")]
    public async Task Remove_Unknown_Returns404(string symbol)
    {
        using var response = await SendAsync(api, HttpMethod.Delete, Path + "/" + symbol, ApiFactory.AdminApiKey);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var problem = await ReadAsync(response);
        problem.Type.ShouldBe(TypeBase + "unknown-symbol");
        problem.Status.ShouldBe(404);
    }

    [Fact]
    public async Task Remove_AlreadyInactive_Returns404()
    {
        using var first = await SendAsync(api, HttpMethod.Delete, Path + "/ETHUSDT", ApiFactory.AdminApiKey);
        first.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        using var second = await SendAsync(api, HttpMethod.Delete, Path + "/ETHUSDT", ApiFactory.AdminApiKey);

        second.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ReadAsync(second)).Type.ShouldBe(TypeBase + "unknown-symbol");
    }

    // ---- list -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task List_ReturnsAllSymbols_IncludingInactive()
    {
        (await SendAsync(api, HttpMethod.Delete, Path + "/ETHUSDT", ApiFactory.AdminApiKey)).Dispose();

        using var response = await SendAsync(api, HttpMethod.Get, Path, ApiFactory.AdminApiKey);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var json = await JsonAsync(response);
        var items = json.RootElement.EnumerateArray().ToArray();
        items.Select(i => i.GetProperty("symbol").GetString()).ShouldBe(["BTCUSDT", "ADAUSDT", "ETHUSDT"], ignoreOrder: true);
        var eth = items.Single(i => i.GetProperty("symbol").GetString() == "ETHUSDT");
        eth.EnumerateObject().Select(p => p.Name)
            .ShouldBe(["symbol", "isActive", "createdAt", "deactivatedAt", "backfillStatus", "backfilledFrom"]);
        eth.GetProperty("isActive").GetBoolean().ShouldBeFalse();
        eth.GetProperty("deactivatedAt").GetString().ShouldNotBeNull().ShouldEndWith("Z");
        eth.GetProperty("backfillStatus").GetString().ShouldBe("NotRequested");
        eth.GetProperty("backfilledFrom").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task List_Xml_UsesTheAdminContract()
    {
        using var response = await SendAsync(api, HttpMethod.Get, Path, ApiFactory.AdminApiKey, accept: "application/xml");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var root = await XmlAsync(response);
        root.Name.ShouldBe(Schema + "adminSymbols");
        var first = root.Elements(Schema + "adminSymbol").First();
        first.Elements().Select(e => e.Name.LocalName)
            .ShouldBe(["symbol", "isActive", "createdAt", "deactivatedAt", "backfillStatus", "backfilledFrom"]);
    }

    // ---- negotiation and rate limiting ------------------------------------------------------------------------

    [Fact]
    public async Task Admin_Problems_HonourXmlAccept()
    {
        using (var unauthorized = await SendAsync(api, HttpMethod.Get, Path, key: null, accept: "application/xml"))
        {
            unauthorized.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            MediaType(unauthorized).ShouldBe(XmlMediaType);
            (await ReadAsync(unauthorized)).Type.ShouldBe(TypeBase + "unauthorized");
        }

        using (var invalid = await SendAsync(api, HttpMethod.Post, Path, ApiFactory.AdminApiKey, body: "BTC-USDT", accept: "application/xml"))
        {
            invalid.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            MediaType(invalid).ShouldBe(XmlMediaType);
            (await ReadAsync(invalid)).Type.ShouldBe(TypeBase + "invalid-symbol");
        }

        using (var unknown = await SendAsync(api, HttpMethod.Delete, Path + "/NOPEUSDT", ApiFactory.AdminApiKey, accept: "application/xml"))
        {
            unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            MediaType(unknown).ShouldBe(XmlMediaType);
            (await ReadAsync(unknown)).Type.ShouldBe(TypeBase + "unknown-symbol");
        }

        await using var disabled = new ApiFactory(api.Database).WithSetting("Admin:ApiKey", "");
        await disabled.InitializeAsync();
        using var forbidden = await SendAsync(disabled, HttpMethod.Get, Path, key: null, accept: "application/xml");
        forbidden.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        MediaType(forbidden).ShouldBe(XmlMediaType);
        (await ReadAsync(forbidden)).Type.ShouldBe(TypeBase + "admin-disabled");
    }

    [Fact]
    public async Task Admin_UnauthorizedStorm_IsRateLimited()
    {
        await using var limited = new ApiFactory(api.Database)
            .WithSetting("RateLimiting:PermitLimit", "3")
            .WithSetting("RateLimiting:WindowSeconds", "3600");
        await limited.InitializeAsync();

        for (var i = 0; i < 3; i++)
        {
            using var rejected = await SendAsync(limited, HttpMethod.Get, Path, key: "guess-" + i);
            rejected.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        // The limiter runs before authentication: further guesses, and even the right key, are throttled.
        foreach (var key in new[] { "guess-3", ApiFactory.AdminApiKey })
        {
            using var throttled = await SendAsync(limited, HttpMethod.Get, Path, key);
            throttled.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
            (await ReadAsync(throttled)).Type.ShouldBe(TypeBase + "rate-limited");
        }
    }

    private static async Task<HttpResponseMessage> SendAsync(
        ApiFactory factory,
        HttpMethod method,
        string path,
        string? key,
        string? body = null,
        string? accept = null,
        bool bodyIsSymbol = false)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        if (key is not null)
        {
            request.Headers.Add(ApiKeyHeader, key);
        }

        if (accept is not null)
        {
            request.Headers.TryAddWithoutValidation("Accept", accept);
        }

        if (body is not null || bodyIsSymbol)
        {
            request.Content = JsonContent.Create(new { symbol = body });
        }

        return await client.SendAsync(request, Ct);
    }

    private static Task<MonitoredSymbol?> FindAsync(ApiFactory factory, string symbol) =>
        factory.Services.GetRequiredService<ISymbolRepository>().FindAsync(
            SymbolName.TryCreate(symbol, out var name) ? name : throw new ArgumentException(symbol, nameof(symbol)),
            Ct);

    private static async Task<JsonDocument> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));

    private static async Task<XElement> XmlAsync(HttpResponseMessage response) =>
        XDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).Root!;
}
