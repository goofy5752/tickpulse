using System.Net;
using System.Text;
using System.Text.Json;
using TickPulse.Integration.Tests.Fixtures;
using static TickPulse.Integration.Tests.Api.ProblemReader;

namespace TickPulse.Integration.Tests.Api;

/// <summary>
/// Errors that the framework raises before an action runs (wrong method, unsupported body type) are TickPulse
/// problems like every other error, and <c>HEAD</c> answers wherever <c>GET</c> does.
/// </summary>
public sealed class HttpMethodTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("/api/probe/BTCUSDT", null, JsonMediaType)]
    [InlineData("/api/probe/BTCUSDT", "application/xml", XmlMediaType)]
    [InlineData("/api/symbols", null, JsonMediaType)]
    public async Task WrongMethod_Returns405ProblemWithAllow(string path, string? accept, string mediaType)
    {
        using var client = api.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative));
        if (accept is not null)
        {
            request.Headers.TryAddWithoutValidation("Accept", accept);
        }

        using var response = await client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.MethodNotAllowed);
        response.Content.Headers.Allow.ShouldContain("GET");
        response.Content.Headers.Allow.ShouldContain("HEAD");
        MediaType(response).ShouldBe(mediaType);
        var problem = await ReadAsync(response);
        problem.Type.ShouldBe(TypeBase + "method-not-allowed");
        problem.Title.ShouldBe("Method not allowed");
        problem.Status.ShouldBe(405);
        problem.Detail.ShouldNotBeNull().ShouldContain("POST");
        problem.TraceId.ShouldNotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData(null, JsonMediaType)]
    [InlineData("application/xml", XmlMediaType)]
    public async Task UnsupportedBody_Returns415Problem(string? accept, string mediaType)
    {
        using var client = api.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/admin/symbols", UriKind.Relative))
        {
            Content = new StringContent("BNBUSDT", Encoding.UTF8, "text/plain"),
        };
        request.Headers.Add("X-Api-Key", ApiFactory.AdminApiKey);
        if (accept is not null)
        {
            request.Headers.TryAddWithoutValidation("Accept", accept);
        }

        using var response = await client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType);
        MediaType(response).ShouldBe(mediaType);
        var problem = await ReadAsync(response);
        problem.Type.ShouldBe(TypeBase + "unsupported-media-type");
        problem.Title.ShouldBe("Unsupported media type");
        problem.Status.ShouldBe(415);
        problem.Detail.ShouldNotBeNull().ShouldContain("application/json");
        problem.TraceId.ShouldNotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("/api/probe/BTCUSDT", "application/json")]
    [InlineData("/api/symbols", "application/json")]
    [InlineData("/health/live", "application/json")]
    public async Task Head_OnGetEndpoint_Returns200WithoutBody(string path, string mediaType)
    {
        using var client = api.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Head, new Uri(path, UriKind.Relative));

        using var response = await client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, path);
        MediaType(response).ShouldBe(mediaType, path);
        (await response.Content.ReadAsByteArrayAsync(Ct)).ShouldBeEmpty(path);
    }

    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData("wrong-key", HttpStatusCode.Unauthorized)]
    [InlineData(ApiFactory.AdminApiKey, HttpStatusCode.OK)]
    public async Task Head_OnAdminEndpoint_NeedsTheKey(string? key, HttpStatusCode expected)
    {
        using var client = api.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Head, new Uri("/api/admin/symbols", UriKind.Relative));
        if (key is not null)
        {
            request.Headers.Add("X-Api-Key", key);
        }

        using var response = await client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(expected);
        if (expected == HttpStatusCode.Unauthorized)
        {
            response.Headers.WwwAuthenticate.ShouldHaveSingleItem().Scheme.ShouldBe("ApiKey");
        }

        (await response.Content.ReadAsByteArrayAsync(Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Head_OnStaticFiles_Returns200WithoutBody()
    {
        const string script = "console.log('tickpulse head test');";
        Directory.CreateDirectory(Path.Combine(api.WebRoot, "assets"));
        await File.WriteAllTextAsync(Path.Combine(api.WebRoot, "assets", "head-Dk3j9a1Q.js"), script, Ct);
        await File.WriteAllTextAsync(Path.Combine(api.WebRoot, "index.html"), "<!doctype html><title>head</title>", Ct);
        using var client = api.CreateClient();

        foreach (var path in new[] { "/assets/head-Dk3j9a1Q.js", "/", "/symbols/BTCUSDT" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, new Uri(path, UriKind.Relative));
            using var response = await client.SendAsync(request, Ct);

            response.StatusCode.ShouldBe(HttpStatusCode.OK, path);
            (await response.Content.ReadAsByteArrayAsync(Ct)).ShouldBeEmpty(path);
        }
    }

    [Fact]
    public async Task OpenApiDocument_DoesNotListHeadOperations()
    {
        using var client = api.CreateClient();

        using var document = JsonDocument.Parse(await client.GetStringAsync(new Uri("/openapi/v1.json", UriKind.Relative), Ct));

        var operations = document.RootElement.GetProperty("paths").EnumerateObject()
            .SelectMany(p => p.Value.EnumerateObject().Select(o => o.Name))
            .ToList();
        operations.ShouldContain("get");
        operations.ShouldNotContain("head");
    }

    [Fact]
    public async Task Head_OnUnknownApiPath_Returns404()
    {
        using var client = api.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Head, new Uri("/api/does/not/exist", UriKind.Relative));

        using var response = await client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
