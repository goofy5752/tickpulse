using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using TickPulse.Api.Hosting;
using TickPulse.Api.Problems;
using TickPulse.Integration.Tests.Fixtures;
using TickPulse.Integration.Tests.Ingestion;
using static TickPulse.Integration.Tests.Api.ProblemReader;

namespace TickPulse.Integration.Tests.Api;

public sealed class NegotiationTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    /// <summary>What a browser sends when the address bar opens an API URL.</summary>
    public const string BrowserAccept = "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8";

    private static readonly XNamespace Schema = "https://tickpulse.dev/schema/v1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Accept header → the status and media type of a successful resource.</summary>
    public static TheoryData<string?, HttpStatusCode, string> SuccessMatrix => new()
    {
        { null, HttpStatusCode.OK, "application/json" },
        { "*/*", HttpStatusCode.OK, "application/json" },
        { "application/json", HttpStatusCode.OK, "application/json" },
        { "application/xml", HttpStatusCode.OK, "application/xml" },
        { "text/xml", HttpStatusCode.OK, "text/xml" },
        { BrowserAccept, HttpStatusCode.OK, "application/xml" },
        { "application/json;q=0.5, application/xml", HttpStatusCode.OK, "application/xml" },
        { "*/*, application/xml", HttpStatusCode.OK, "application/xml" },
        { "text/csv", HttpStatusCode.NotAcceptable, JsonMediaType },
    };

    /// <summary>
    /// Accept header → the media type of a 404 problem. An error is never turned into a 406: a client that accepts
    /// neither JSON nor XML gets the problem as JSON.
    /// </summary>
    public static TheoryData<string?, string> ProblemMatrix => new()
    {
        { null, JsonMediaType },
        { "*/*", JsonMediaType },
        { "application/json", JsonMediaType },
        { "application/xml", XmlMediaType },
        { "text/xml", XmlMediaType },
        { BrowserAccept, XmlMediaType },
        { "application/json;q=0.5, application/xml", XmlMediaType },
        { "*/*, application/xml", XmlMediaType },
        { "text/csv", JsonMediaType },
    };

    [Theory]
    [MemberData(nameof(SuccessMatrix))]
    public async Task Negotiation_Matrix(string? accept, HttpStatusCode status, string mediaType)
    {
        using var client = api.CreateClient();
        using var request = Get("/api/probe/BTCUSDT", accept);

        using var response = await client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(status);
        MediaType(response).ShouldBe(mediaType);
        var body = await response.Content.ReadAsStringAsync(Ct);
        switch (mediaType)
        {
            case "application/json":
                using (var json = JsonDocument.Parse(body))
                {
                    json.RootElement.GetProperty("symbol").GetString().ShouldBe("BTCUSDT");
                    json.RootElement.GetProperty("price").GetDecimal().ShouldBe(64_123.12345678m);
                    json.RootElement.GetProperty("at").GetString().ShouldBe("2026-03-02T12:00:00Z");
                }

                break;
            case "application/xml" or "text/xml":
                var root = XDocument.Parse(body).Root!;
                root.Name.ShouldBe(Schema + "probe");
                root.Element(Schema + "symbol")!.Value.ShouldBe("BTCUSDT");
                root.Element(Schema + "price")!.Value.ShouldBe("64123.12345678");
                root.Element(Schema + "at")!.Value.ShouldBe("2026-03-02T12:00:00Z");
                break;
            default:
                var problem = await ReadAsync(response);
                problem.Type.ShouldBe(TypeBase + "not-acceptable");
                problem.Status.ShouldBe(406);
                problem.Title.ShouldNotBeNullOrWhiteSpace();
                problem.TraceId.ShouldNotBeNullOrWhiteSpace();
                break;
        }
    }

    [Theory]
    [MemberData(nameof(ProblemMatrix))]
    public async Task Negotiation_Matrix_OnProblem(string? accept, string mediaType)
    {
        using var client = api.CreateClient();
        using var request = Get("/api/probe/NOPEUSDT", accept);

        using var response = await client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        MediaType(response).ShouldBe(mediaType);
        var problem = await ReadAsync(response);
        problem.Type.ShouldBe(TypeBase + "unknown-symbol");
        problem.Title.ShouldBe("Unknown symbol");
        problem.Status.ShouldBe(404);
        problem.Detail.ShouldBe("Symbol 'NOPEUSDT' is not monitored.");
        problem.TraceId.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task UnmatchedRoute_Xml_ReturnsProblemXml()
    {
        using var client = api.CreateClient();
        using var request = Get("/api/does/not/exist", "application/xml");

        using var response = await client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        MediaType(response).ShouldBe(XmlMediaType);
        var problem = await ReadAsync(response);
        problem.Type.ShouldBe(TypeBase + "not-found");
        problem.Title.ShouldBe("Not found");
        problem.Status.ShouldBe(404);
        problem.TraceId.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task UnmatchedRoute_NoAccept_ReturnsProblemJson()
    {
        using var client = api.CreateClient();

        using var response = await client.GetAsync(new Uri("/api/does/not/exist", UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        MediaType(response).ShouldBe(JsonMediaType);
        (await ReadAsync(response)).Status.ShouldBe(404);
    }

    [Theory]
    [InlineData(null, JsonMediaType)]
    [InlineData("application/xml", XmlMediaType)]
    public async Task UnhandledException_ReturnsProblemWithoutStackTrace(string? accept, string mediaType)
    {
        using var client = api.CreateClient();
        using var request = Get("/api/probe/throw", accept);

        using var response = await client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        MediaType(response).ShouldBe(mediaType);
        var problem = await ReadAsync(response);
        problem.Type.ShouldBe(TypeBase + "internal");
        problem.Status.ShouldBe(500);
        problem.Detail.ShouldBe("An unexpected error occurred.");
        problem.TraceId.ShouldNotBeNullOrWhiteSpace();
        problem.Raw.ShouldNotContain(ProbeController.SecretMessage);
        problem.Raw.ShouldNotContain(nameof(InvalidOperationException));
        problem.Raw.ShouldNotContain(nameof(ProbeController));
    }

    [Fact]
    public async Task Problem_TraceId_MatchesTraceparent()
    {
        const string traceId = "4bf92f3577b34da6a3ce929d0e0e4736";
        using var client = api.CreateClient();
        using var request = Get("/api/probe/NOPEUSDT", null);
        request.Headers.Add("traceparent", $"00-{traceId}-00f067aa0ba902b7-01");

        using var response = await client.SendAsync(request, Ct);

        (await ReadAsync(response)).TraceId.ShouldNotBeNull().ShouldContain(traceId);
    }

    [Fact]
    public async Task RateLimit_Exceeded_Returns429Problem()
    {
        await using var limited = new ApiFactory(api.Database)
            .WithSetting("RateLimiting:PermitLimit", "2")
            .WithSetting("RateLimiting:WindowSeconds", "3600");
        await limited.InitializeAsync();
        using var client = limited.CreateClient();

        for (var i = 0; i < 2; i++)
        {
            using var ok = await client.GetAsync(new Uri("/api/probe/BTCUSDT", UriKind.Relative), Ct);
            ok.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using var json = await client.GetAsync(new Uri("/api/probe/BTCUSDT", UriKind.Relative), Ct);
        using var xmlRequest = Get("/api/probe/BTCUSDT", "application/xml");
        using var xml = await client.SendAsync(xmlRequest, Ct);

        json.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        MediaType(json).ShouldBe(JsonMediaType);
        json.Headers.RetryAfter.ShouldNotBeNull().Delta.ShouldNotBeNull().ShouldBeGreaterThan(TimeSpan.Zero);
        var problem = await ReadAsync(json);
        problem.Type.ShouldBe(TypeBase + "rate-limited");
        problem.Status.ShouldBe(429);
        problem.TraceId.ShouldNotBeNullOrWhiteSpace();

        xml.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        MediaType(xml).ShouldBe(XmlMediaType);
        (await ReadAsync(xml)).Type.ShouldBe(TypeBase + "rate-limited");

        // Health and docs are not rate limited.
        using var health = await client.GetAsync(new Uri("/health/live", UriKind.Relative), Ct);
        health.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task RateLimit_RetryAfter_IsTheTimeLeftInTheWindow()
    {
        await using var limited = new ApiFactory(api.Database)
            .WithSetting("RateLimiting:PermitLimit", "1")
            .WithSetting("RateLimiting:WindowSeconds", "60");
        await limited.InitializeAsync();
        using var client = limited.CreateClient();
        var probe = new Uri("/api/probe/BTCUSDT", UriKind.Relative);

        (await client.GetAsync(probe, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        limited.Clock.Advance(TimeSpan.FromSeconds(25));
        using var early = await client.GetAsync(probe, Ct);
        limited.Clock.Advance(TimeSpan.FromSeconds(34.5));
        using var late = await client.GetAsync(probe, Ct);
        limited.Clock.Advance(TimeSpan.FromSeconds(0.5));
        using var next = await client.GetAsync(probe, Ct);

        early.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        early.Headers.RetryAfter.ShouldNotBeNull().Delta.ShouldBe(TimeSpan.FromSeconds(35));
        (await ReadAsync(early)).Detail.ShouldBe("Too many requests from this client; retry after 35 s.");
        late.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        late.Headers.RetryAfter.ShouldNotBeNull().Delta.ShouldBe(TimeSpan.FromSeconds(1), "rounded up, never 0");
        next.StatusCode.ShouldBe(HttpStatusCode.OK, "a new window has started");
    }

    [Fact]
    public async Task RateLimit_Rejections_LogOneWarningPerClientPerWindow()
    {
        var logs = new IngestionTestHost.LogCapture();
        await using var limited = new ApiFactory(api.Database)
            .WithSetting("RateLimiting:PermitLimit", "1")
            .WithSetting("RateLimiting:WindowSeconds", "60")
            .WithLogging(logs);
        await limited.InitializeAsync();
        var first = IPAddress.Parse("203.0.113.61");
        var second = IPAddress.Parse("203.0.113.62");

        (await SendAsync(limited, first, visitor: null)).ShouldBe(200);
        for (var i = 0; i < 5; i++)
        {
            (await SendAsync(limited, first, visitor: null)).ShouldBe(429);
        }

        Warnings().Count.ShouldBe(1);

        // Another client gets its own warning; the first client's next window gets a new one.
        (await SendAsync(limited, second, visitor: null)).ShouldBe(200);
        (await SendAsync(limited, second, visitor: null)).ShouldBe(429);
        limited.Clock.Advance(TimeSpan.FromSeconds(60));
        (await SendAsync(limited, first, visitor: null)).ShouldBe(200);
        (await SendAsync(limited, first, visitor: null)).ShouldBe(429);
        (await SendAsync(limited, first, visitor: null)).ShouldBe(429);

        var warnings = Warnings();
        warnings.Count.ShouldBe(3);
        warnings.ShouldAllBe(e => e.Message.Contains("/api/probe/BTCUSDT", StringComparison.Ordinal));
        warnings.ShouldNotContain(e => e.Message.Contains(first.ToString(), StringComparison.Ordinal));

        List<(string Category, LogLevel Level, string EventName, string Message)> Warnings() =>
            [.. logs.Entries.Where(e => e.Category == "TickPulse.Api.Hosting.ApiRateLimiting" && e.Level >= LogLevel.Warning)];
    }

    [Fact]
    public async Task RateLimit_PartitionsByCfConnectingIp_OnlyFromTrustedProxy()
    {
        await using var limited = new ApiFactory(api.Database)
            .WithSetting("RateLimiting:PermitLimit", "1")
            .WithSetting("RateLimiting:WindowSeconds", "3600");
        await limited.InitializeAsync();

        // Through the trusted loopback proxy, each visitor IP has its own window.
        (await SendAsync(limited, IPAddress.Loopback, "198.51.100.1")).ShouldBe(200);
        (await SendAsync(limited, IPAddress.Loopback, "198.51.100.2")).ShouldBe(200);
        (await SendAsync(limited, IPAddress.Loopback, "198.51.100.1")).ShouldBe(429);

        // From an untrusted peer the header is ignored, so both requests share the peer's window.
        var untrusted = IPAddress.Parse("203.0.113.7");
        (await SendAsync(limited, untrusted, "198.51.100.3")).ShouldBe(200);
        (await SendAsync(limited, untrusted, "198.51.100.4")).ShouldBe(429);
    }

    [Fact]
    public async Task RateLimit_TrustsConfiguredProxy()
    {
        var proxy = IPAddress.Parse("10.0.0.5");
        await using var limited = new ApiFactory(api.Database)
            .WithSetting("RateLimiting:PermitLimit", "1")
            .WithSetting("ForwardedHeaders:KnownProxies:0", proxy.ToString());
        await limited.InitializeAsync();

        (await SendAsync(limited, proxy, "198.51.100.1")).ShouldBe(200);
        (await SendAsync(limited, proxy, "198.51.100.2")).ShouldBe(200);
    }

    [Fact]
    public void ProblemWriters_AreXmlThenJsonOnly() =>
        api.Services.GetServices<IProblemDetailsWriter>().Select(w => w.GetType())
            .ShouldBe([typeof(XmlProblemDetailsWriter), typeof(JsonProblemDetailsWriter)]);

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("*/*", false)]
    [InlineData("application/json", false)]
    [InlineData("application/problem+json", false)]
    [InlineData("application/*", false)]
    [InlineData("text/csv", false)]
    [InlineData("application/xml", true)]
    [InlineData("text/xml", true)]
    [InlineData("application/problem+xml", true)]
    [InlineData(BrowserAccept, true)]
    [InlineData("application/json;q=0.5, application/xml", true)]
    [InlineData("application/xml;q=0.5, application/json", false)]
    [InlineData("application/xml, application/json", true)]
    [InlineData("application/json, application/xml", false)]
    [InlineData("application/xml;q=0, */*", false)]
    [InlineData("text/csv, application/xml;q=0.1", true)]
    [InlineData("*/*;q=0.9, text/xml", true)]
    [InlineData("*/*, application/xml", true)]
    [InlineData("application/*, text/xml", true)]
    [InlineData("text/*, application/problem+xml", true)]
    [InlineData("application/json, */*, application/xml", false)]
    [InlineData("application/xml, */*, application/json", true)]
    [InlineData("*/*, application/xml;q=0.9", false)]
    public void PrefersXml_HonoursQualityAndOrder(string? accept, bool expected) =>
        XmlProblemDetailsWriter.PrefersXml(accept is null ? StringValues.Empty : new StringValues(accept)).ShouldBe(expected);

    [Theory]
    [InlineData("*/*, application/xml", "application/xml, */*")]
    [InlineData("application/*, text/xml", "text/xml, application/*")]
    [InlineData("*/*;q=0.8, text/*, application/json;q=0.9", "text/*, application/json; q=0.9, */*; q=0.8")]
    [InlineData("application/json, application/xml", null)]
    [InlineData("application/xml, */*", null)]
    [InlineData("application/json", null)]
    [InlineData(null, null)]
    public void AcceptNormalization_OrdersByQualityThenSpecificity(string? accept, string? expected) =>
        AcceptHeaderNormalization.Normalize(accept is null ? StringValues.Empty : new StringValues(accept)).ShouldBe(expected);

    [Fact]
    public async Task RateLimit_TrustsConfiguredNetwork()
    {
        // In a container the proxy is the Docker bridge gateway, not loopback.
        var gateway = IPAddress.Parse("172.18.0.1");
        await using var limited = new ApiFactory(api.Database)
            .WithSetting("RateLimiting:PermitLimit", "1")
            .WithSetting("ForwardedHeaders:KnownNetworks:0", "172.16.0.0/12");
        await limited.InitializeAsync();

        (await SendAsync(limited, gateway, "198.51.100.1")).ShouldBe(200);
        (await SendAsync(limited, gateway, "198.51.100.2")).ShouldBe(200);
        (await SendAsync(limited, gateway, "198.51.100.1")).ShouldBe(429);

        // Outside the configured network the header is still ignored.
        var outsider = IPAddress.Parse("192.0.2.9");
        (await SendAsync(limited, outsider, "198.51.100.3")).ShouldBe(200);
        (await SendAsync(limited, outsider, "198.51.100.4")).ShouldBe(429);
    }

    [Fact]
    public async Task ForwardedHeaders_InvalidKnownNetwork_FailsStartup()
    {
        await using var invalid = new ApiFactory(api.Database).WithSetting("ForwardedHeaders:KnownNetworks", "172.16.0.0/12, not-a-cidr");
        await invalid.InitializeAsync();

        Should.Throw<InvalidOperationException>(() => invalid.CreateClient()).Message.ShouldContain("not-a-cidr");
    }

    [Fact]
    public async Task RateLimit_IgnoresXForwardedFor_EvenFromLoopback()
    {
        await using var limited = new ApiFactory(api.Database).WithSetting("RateLimiting:PermitLimit", "1");
        await limited.InitializeAsync();

        // Only CF-Connecting-IP names the client; a spoofed X-Forwarded-For must not open a new window.
        (await SendAsync(limited, IPAddress.Loopback, "198.51.100.1", header: "X-Forwarded-For")).ShouldBe(200);
        (await SendAsync(limited, IPAddress.Loopback, "198.51.100.2", header: "X-Forwarded-For")).ShouldBe(429);
    }

    [Fact]
    public async Task RateLimit_Ipv6Clients_SharePerSlash64()
    {
        await using var limited = new ApiFactory(api.Database).WithSetting("RateLimiting:PermitLimit", "1");
        await limited.InitializeAsync();

        (await SendAsync(limited, IPAddress.Parse("2001:db8:1:2::10"), visitor: null)).ShouldBe(200);
        (await SendAsync(limited, IPAddress.Parse("2001:db8:1:2:ffff::20"), visitor: null)).ShouldBe(429);
        (await SendAsync(limited, IPAddress.Parse("2001:db8:1:3::10"), visitor: null)).ShouldBe(200);
    }

    [Theory]
    [InlineData("198.51.100.23", "198.51.100.23")]
    [InlineData("::ffff:198.51.100.23", "198.51.100.23")]
    [InlineData("2001:db8:1:2:aaaa:bbbb:cccc:dddd", "2001:db8:1:2::/64")]
    [InlineData("::1", "::/64")]
    [InlineData(null, "unknown")]
    public void PartitionKey_GroupsIpv6ByPrefix(string? address, string expected) =>
        ClientAddressing.PartitionKey(address is null ? null : IPAddress.Parse(address)).ShouldBe(expected);

    [Fact]
    public async Task RateLimit_DoesNotApplyToDocsHealthOrOpenApi()
    {
        await using var limited = new ApiFactory(api.Database).WithSetting("RateLimiting:PermitLimit", "1");
        await limited.InitializeAsync();
        var peer = IPAddress.Parse("203.0.113.50");

        (await SendAsync(limited, peer, visitor: null)).ShouldBe(200);
        (await SendAsync(limited, peer, visitor: null)).ShouldBe(429);
        foreach (var path in new[] { "/docs/", "/docs/scalar.js", "/openapi/v1.json", "/health/live", "/health/ready" })
        {
            for (var i = 0; i < 3; i++)
            {
                (await SendAsync(limited, peer, visitor: null, path: path)).ShouldBe(200, path);
            }
        }
    }

    [Theory]
    [InlineData("/api/probe/BTCUSDT", null)]
    [InlineData("/api/probe/NOPEUSDT", "application/xml")]
    [InlineData("/api/does/not/exist", null)]
    [InlineData("/api/probe/BTCUSDT", "text/csv")]
    public async Task ApiResponses_VaryByAccept(string path, string? accept)
    {
        using var client = api.CreateClient();
        using var request = Get(path, accept);

        using var response = await client.SendAsync(request, Ct);

        response.Headers.Vary.ShouldContain("Accept");
    }

    [Fact]
    public async Task NonApiResponses_DoNotVaryByAccept()
    {
        using var client = api.CreateClient();

        using var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative), Ct);

        response.Headers.Vary.ShouldNotContain("Accept");
    }

    [Theory]
    [InlineData(null, JsonMediaType)]
    [InlineData("application/xml", XmlMediaType)]
    [InlineData("text/html", JsonMediaType)]
    public async Task UnhandledException_InProduction_LeaksNothing(string? accept, string mediaType)
    {
        await using var production = new ApiFactory(api.Database).WithEnvironment("Production");
        await production.InitializeAsync();
        using var client = production.CreateClient();
        using var request = Get("/api/probe/throw", accept);

        using var response = await client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        MediaType(response).ShouldBe(mediaType);
        var problem = await ReadAsync(response);
        problem.Type.ShouldBe(TypeBase + "internal");
        problem.Detail.ShouldBe("An unexpected error occurred.");
        foreach (var leak in new[] { ProbeController.SecretMessage, nameof(InvalidOperationException), nameof(ProbeController), " at ", "\\" })
        {
            problem.Raw.ShouldNotContain(leak);
        }
    }

    private static async Task<int> SendAsync(
        ApiFactory factory,
        IPAddress peer,
        string? visitor,
        string path = "/api/probe/BTCUSDT",
        string header = "CF-Connecting-IP")
    {
        var context = await factory.Server.SendAsync(
            c =>
            {
                c.Request.Method = HttpMethods.Get;
                c.Request.Path = path;
                if (visitor is not null)
                {
                    c.Request.Headers[header] = visitor;
                }

                c.Connection.RemoteIpAddress = peer;
            },
            Ct);
        return context.Response.StatusCode;
    }
}
