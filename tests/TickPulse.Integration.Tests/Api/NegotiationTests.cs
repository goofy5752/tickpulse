using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using TickPulse.Api.Problems;
using TickPulse.Integration.Tests.Fixtures;
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
                root.Element(Schema + "Symbol")!.Value.ShouldBe("BTCUSDT");
                root.Element(Schema + "Price")!.Value.ShouldBe("64123.12345678");
                root.Element(Schema + "At")!.Value.ShouldBe("2026-03-02T12:00:00Z");
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
        problem.Type.ShouldBe("about:blank");
        problem.Title.ShouldBe("Not Found");
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
    public void PrefersXml_HonoursQualityAndOrder(string? accept, bool expected) =>
        XmlProblemDetailsWriter.PrefersXml(accept is null ? StringValues.Empty : new StringValues(accept)).ShouldBe(expected);

    private static async Task<int> SendAsync(ApiFactory factory, IPAddress peer, string visitor)
    {
        var context = await factory.Server.SendAsync(
            c =>
            {
                c.Request.Method = HttpMethods.Get;
                c.Request.Path = "/api/probe/BTCUSDT";
                c.Request.Headers["CF-Connecting-IP"] = visitor;
                c.Connection.RemoteIpAddress = peer;
            },
            Ct);
        return context.Response.StatusCode;
    }
}
