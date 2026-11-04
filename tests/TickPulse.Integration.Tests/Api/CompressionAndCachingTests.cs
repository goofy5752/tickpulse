using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.Primitives;
using TickPulse.Api.Hosting;
using TickPulse.Integration.Tests.Fixtures;
using static TickPulse.Integration.Tests.Api.ProblemReader;

namespace TickPulse.Integration.Tests.Api;

/// <summary>
/// Responses are Brotli- or gzip-compressed when the client asks for it, caches keep the encodings apart, and the
/// dashboard's fingerprinted assets are cached for a year while <c>index.html</c> is always revalidated.
/// </summary>
public sealed class CompressionAndCachingTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    private const string AssetPath = "/assets/index-Cpoy5iO2.js";
    private const string StylePath = "/assets/index-B2kq0x9Z.css";

    private static readonly XNamespace Schema = "https://tickpulse.dev/schema/v1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("br", "br")]
    [InlineData("gzip", "gzip")]
    [InlineData("gzip, deflate, br", "br")]
    [InlineData("br;q=0.5, gzip", "gzip")]
    public async Task ApiJson_IsCompressed_AndVariesByAcceptAndEncoding(string acceptEncoding, string expected)
    {
        using var client = api.CreateClient();
        using var request = Request("/api/probe/BTCUSDT", acceptEncoding);

        using var response = await client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentEncoding.ShouldBe([expected]);
        response.Headers.Vary.ShouldContain("Accept");
        response.Headers.Vary.ShouldContain("Accept-Encoding");
        using var json = JsonDocument.Parse(await DecodeAsync(response));
        json.RootElement.GetProperty("symbol").GetString().ShouldBe("BTCUSDT");
    }

    [Fact]
    public async Task ApiXmlAndProblems_AreCompressed()
    {
        using var client = api.CreateClient();
        using var xmlRequest = Request("/api/probe/BTCUSDT", "gzip", accept: "application/xml");
        using var problemRequest = Request("/api/probe/NOPEUSDT", "br", accept: "application/xml");

        using var xml = await client.SendAsync(xmlRequest, Ct);
        using var problem = await client.SendAsync(problemRequest, Ct);

        xml.Content.Headers.ContentEncoding.ShouldBe(["gzip"]);
        XDocument.Parse(await DecodeAsync(xml)).Root!.Name.ShouldBe(Schema + "probe");
        problem.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        MediaType(problem).ShouldBe(XmlMediaType);
        problem.Content.Headers.ContentEncoding.ShouldBe(["br"]);
        (await DecodeAsync(problem)).ShouldContain(TypeBase + "unknown-symbol");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("identity")]
    public async Task WithoutAcceptEncoding_IsNotCompressed(string? acceptEncoding)
    {
        using var client = api.CreateClient();
        using var request = Request("/api/probe/BTCUSDT", acceptEncoding);

        using var response = await client.SendAsync(request, Ct);

        response.Content.Headers.ContentEncoding.ShouldBeEmpty();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        json.RootElement.GetProperty("symbol").GetString().ShouldBe("BTCUSDT");
    }

    [Theory]
    [InlineData(AssetPath, "text/javascript")]
    [InlineData(StylePath, "text/css")]
    public async Task FingerprintedAsset_IsImmutable_AndCompressed(string path, string mediaType)
    {
        var content = await WriteAsync(path, Filler(path));
        using var client = api.CreateClient();
        using var request = Request(path, "br, gzip");

        using var response = await client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        MediaType(response).ShouldBe(mediaType);
        var cache = response.Headers.CacheControl.ShouldNotBeNull();
        cache.Public.ShouldBeTrue();
        cache.MaxAge.ShouldBe(TimeSpan.FromDays(365));
        cache.Extensions.ShouldContain(e => e.Name == "immutable");
        cache.NoCache.ShouldBeFalse();
        response.Content.Headers.ContentEncoding.ShouldBe(["br"]);
        response.Headers.Vary.ShouldContain("Accept-Encoding");
        (await DecodeAsync(response)).ShouldBe(content);
    }

    [Fact]
    public async Task IndexHtml_IsAlwaysRevalidated()
    {
        await WriteAsync("/index.html", "<!doctype html><title>tickpulse-cache-test</title>" + Filler("index"));
        using var client = api.CreateClient();

        foreach (var path in new[] { "/", "/index.html", "/symbols/BTCUSDT" })
        {
            using var request = Request(path, "gzip");
            using var response = await client.SendAsync(request, Ct);

            response.StatusCode.ShouldBe(HttpStatusCode.OK, path);
            MediaType(response).ShouldBe("text/html", path);
            var cache = response.Headers.CacheControl.ShouldNotBeNull(path);
            cache.NoCache.ShouldBeTrue(path);
            cache.MaxAge.ShouldBeNull(path);
            cache.Extensions.ShouldNotContain(e => e.Name == "immutable", path);
            response.Content.Headers.ContentEncoding.ShouldBe(["gzip"], path);
            (await DecodeAsync(response)).ShouldContain("tickpulse-cache-test", Case.Sensitive, path);
        }
    }

    [Fact]
    public async Task CompressedAsset_HasWeakETag_AndStillRevalidates()
    {
        const string path = "/assets/etag-Q1w2e3R4.js";
        await WriteAsync(path, Filler(path));
        using var client = api.CreateClient();
        using var plainRequest = Request(path, acceptEncoding: null);
        using var compressedRequest = Request(path, "gzip");

        using var plain = await client.SendAsync(plainRequest, Ct);
        using var compressed = await client.SendAsync(compressedRequest, Ct);

        // A compressed body is another representation, so it must not share the plain file's strong validator.
        var strong = plain.Headers.ETag.ShouldNotBeNull();
        strong.IsWeak.ShouldBeFalse();
        var weak = compressed.Headers.ETag.ShouldNotBeNull();
        weak.IsWeak.ShouldBeTrue();
        weak.Tag.ShouldBe(strong.Tag);

        using var revalidate = Request(path, "gzip");
        revalidate.Headers.TryAddWithoutValidation("If-None-Match", weak.ToString());
        using var notModified = await client.SendAsync(revalidate, Ct);
        notModified.StatusCode.ShouldBe(HttpStatusCode.NotModified);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("\"abc\"", null)]
    [InlineData("*", null)]
    [InlineData("not an etag", null)]
    [InlineData("W/\"abc\"", "\"abc\"")]
    [InlineData("\"abc\", W/\"def\"", "\"abc\", \"def\"")]
    public void WeakIfNoneMatch_IsComparedAsStrong(string? ifNoneMatch, string? expected) =>
        CompressionAndCaching.WeakIfNoneMatchAsStrong(ifNoneMatch is null ? StringValues.Empty : new StringValues(ifNoneMatch))
            .ShouldBe(expected);

    [Fact]
    public async Task MissingAsset_IsNotCachedAsImmutable()
    {
        using var client = api.CreateClient();

        using var response = await client.GetAsync(new Uri("/assets/missing-AbCdEf12.js", UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (response.Headers.CacheControl?.Extensions ?? []).ShouldNotContain(e => e.Name == "immutable");
    }

    private static HttpRequestMessage Request(string path, string? acceptEncoding, string? accept = null)
    {
        var request = Get(path, accept);
        if (acceptEncoding is not null)
        {
            request.Headers.TryAddWithoutValidation("Accept-Encoding", acceptEncoding);
        }

        return request;
    }

    private static async Task<string> DecodeAsync(HttpResponseMessage response)
    {
        await using var raw = await response.Content.ReadAsStreamAsync(Ct);
        await using Stream decoded = response.Content.Headers.ContentEncoding.SingleOrDefault() switch
        {
            "br" => new BrotliStream(raw, CompressionMode.Decompress),
            "gzip" => new GZipStream(raw, CompressionMode.Decompress),
            var other => throw new ShouldAssertException($"Unexpected Content-Encoding '{other}'."),
        };
        using var reader = new StreamReader(decoded, Encoding.UTF8);
        return await reader.ReadToEndAsync(Ct);
    }

    // A few KB of repetitive text, like a real bundle.
    private static string Filler(string seed) =>
        string.Concat(Enumerable.Range(0, 200).Select(i => $"/* {seed} line {i} */\n"));

    private async Task<string> WriteAsync(string path, string content)
    {
        var file = Path.Combine(api.WebRoot, path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, content, Ct);
        return content;
    }
}
