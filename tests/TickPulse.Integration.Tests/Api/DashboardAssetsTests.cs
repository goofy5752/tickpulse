using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using TickPulse.Api.Hosting;
using TickPulse.Integration.Tests.Fixtures;
using static TickPulse.Integration.Tests.Api.ProblemReader;

namespace TickPulse.Integration.Tests.Api;

/// <summary>
/// The dashboard's static files: build-time precompressed assets served by <c>MapStaticAssets</c> with a strong ETag
/// per encoding, a year of <c>immutable</c> caching for Vite's hashed <c>/assets/</c>, <c>no-cache</c> for
/// <c>index.html</c>, and API responses that the host never compresses.
/// </summary>
public sealed class DashboardAssetsTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    private const string AssetPath = "/assets/index-Cpoy5iO2.js";
    private const string StylePath = "/assets/index-B2kq0x9Z.css";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- the real host: API responses, and files the build manifest does not list (a web root filled later) ----

    [Theory]
    [InlineData("/api/probe/BTCUSDT", "br")]
    [InlineData("/api/probe/BTCUSDT", "gzip, deflate, br")]
    [InlineData("/api/probe/NOPEUSDT", "gzip")]
    public async Task ApiResponses_AreNeverCompressedByTheHost(string path, string acceptEncoding)
    {
        using var client = api.CreateClient();
        using var request = Request(path, acceptEncoding);

        using var response = await client.SendAsync(request, Ct);

        response.Content.Headers.ContentEncoding.ShouldBeEmpty();
        response.Headers.Vary.ShouldContain("Accept");
        response.Headers.Vary.ShouldNotContain("Accept-Encoding");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)); // plain JSON, not a compressed body
        json.RootElement.ValueKind.ShouldBe(JsonValueKind.Object);
    }

    [Theory]
    [InlineData(AssetPath, "text/javascript")]
    [InlineData(StylePath, "text/css")]
    public async Task UnmanifestedAsset_IsImmutable(string path, string mediaType)
    {
        await WriteAsync(api.WebRoot, path, Filler(path));
        using var client = api.CreateClient();

        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        MediaType(response).ShouldBe(mediaType);
        ShouldBeImmutable(response);
    }

    [Fact]
    public async Task IndexHtml_IsAlwaysRevalidated()
    {
        await WriteAsync(api.WebRoot, "/index.html", "<!doctype html><title>tickpulse-cache-test</title>");
        using var client = api.CreateClient();

        foreach (var path in new[] { "/", "/index.html", "/symbols/BTCUSDT" })
        {
            using var response = await client.GetAsync(new Uri(path, UriKind.Relative), Ct);

            response.StatusCode.ShouldBe(HttpStatusCode.OK, path);
            MediaType(response).ShouldBe("text/html", path);
            ShouldBeRevalidated(response, path);
        }
    }

    [Fact]
    public async Task MissingAsset_IsNotCachedAsImmutable()
    {
        using var client = api.CreateClient();

        using var response = await client.GetAsync(new Uri("/assets/missing-AbCdEf12.js", UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (response.Headers.CacheControl?.Extensions ?? []).ShouldNotContain(e => e.Name == "immutable");
    }

    // ---- MapStaticAssets with a build manifest, as the published image serves the dashboard ------------------

    [Theory]
    [InlineData("br", "br")]
    [InlineData("gzip", "gzip")]
    [InlineData("gzip, br", "br")]
    [InlineData(null, null)]
    public async Task PrecompressedAsset_IsServedPerEncoding_Immutable(string? acceptEncoding, string? expected)
    {
        await using var host = await StaticAssetsHost.StartAsync();
        using var request = Request(AssetPath, acceptEncoding);

        using var response = await host.Client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        MediaType(response).ShouldBe("text/javascript");
        response.Content.Headers.ContentEncoding.ShouldBe(expected is null ? [] : [expected]);
        response.Headers.Vary.ShouldContain("Accept-Encoding");
        response.Headers.ETag.ShouldNotBeNull().ToString().ShouldBe(host.ETag(AssetPath, expected));
        ShouldBeImmutable(response);
        (await DecodeAsync(response)).ShouldBe(StaticAssetsHost.Content(AssetPath));
    }

    [Theory]
    [InlineData("br")]
    [InlineData("gzip")]
    [InlineData(null)]
    public async Task PrecompressedAsset_ConditionalGet_Returns304WithTheSameETag(string? encoding)
    {
        await using var host = await StaticAssetsHost.StartAsync();
        using var first = await host.Client.SendAsync(Request(AssetPath, encoding), Ct);
        var etag = first.Headers.ETag.ShouldNotBeNull();
        using var revalidate = Request(AssetPath, encoding);
        revalidate.Headers.IfNoneMatch.Add(etag);

        using var second = await host.Client.SendAsync(revalidate, Ct);

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        second.StatusCode.ShouldBe(HttpStatusCode.NotModified);
        second.Headers.ETag.ShouldBe(etag);
        etag.IsWeak.ShouldBeFalse();
        ShouldBeImmutable(second);
        (await second.Content.ReadAsByteArrayAsync(Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task PrecompressedIndexHtml_IsRevalidated()
    {
        await using var host = await StaticAssetsHost.StartAsync();
        using var first = await host.Client.SendAsync(Request("/index.html", "br"), Ct);
        using var revalidate = Request("/index.html", "br");
        revalidate.Headers.IfNoneMatch.Add(first.Headers.ETag.ShouldNotBeNull());

        using var second = await host.Client.SendAsync(revalidate, Ct);

        first.Content.Headers.ContentEncoding.ShouldBe(["br"]);
        ShouldBeRevalidated(first, "/index.html");
        second.StatusCode.ShouldBe(HttpStatusCode.NotModified);
        ShouldBeRevalidated(second, "/index.html");
    }

    private static void ShouldBeImmutable(HttpResponseMessage response)
    {
        var cache = response.Headers.CacheControl.ShouldNotBeNull();
        cache.Public.ShouldBeTrue();
        cache.MaxAge.ShouldBe(TimeSpan.FromDays(365));
        cache.Extensions.ShouldContain(e => e.Name == "immutable");
        cache.NoCache.ShouldBeFalse();
    }

    private static void ShouldBeRevalidated(HttpResponseMessage response, string path)
    {
        var cache = response.Headers.CacheControl.ShouldNotBeNull(path);
        cache.NoCache.ShouldBeTrue(path);
        cache.MaxAge.ShouldBeNull(path);
        cache.Extensions.ShouldNotContain(e => e.Name == "immutable", path);
    }

    private static HttpRequestMessage Request(string path, string? acceptEncoding)
    {
        var request = Get(path, accept: null);
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
            null => raw,
            var other => throw new ShouldAssertException($"Unexpected Content-Encoding '{other}'."),
        };
        using var reader = new StreamReader(decoded, Encoding.UTF8);
        return await reader.ReadToEndAsync(Ct);
    }

    // A few KB of repetitive text, like a real bundle.
    private static string Filler(string seed) =>
        string.Concat(Enumerable.Range(0, 200).Select(i => $"/* {seed} line {i} */\n"));

    private static async Task WriteAsync(string root, string path, string content)
    {
        var file = Path.Combine(root, path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, content, Ct);
    }

    /// <summary>
    /// A host with the dashboard pipeline of <c>Program</c> (<see cref="DashboardAssets"/>) over a web root whose
    /// files, <c>.br</c>/<c>.gz</c> copies and endpoint manifest are laid out as the SDK publishes them.
    /// </summary>
    private sealed class StaticAssetsHost : IAsyncDisposable
    {
        private static readonly (string Path, string Type, string Content)[] Files =
        [
            (AssetPath, "text/javascript", Filler(AssetPath)),
            ("/index.html", "text/html", "<!doctype html><title>tickpulse</title><script src=\"" + AssetPath + "\"></script>"),
        ];

        private readonly WebApplication _app;
        private readonly string _root;
        private readonly Dictionary<(string, string?), string> _etags;

        private StaticAssetsHost(WebApplication app, string root, Dictionary<(string, string?), string> etags)
        {
            _app = app;
            _root = root;
            _etags = etags;
            Client = app.GetTestClient();
        }

        public HttpClient Client { get; }

        public static async Task<StaticAssetsHost> StartAsync()
        {
            var root = Directory.CreateTempSubdirectory("tickpulse-assets-").FullName;
            var etags = new Dictionary<(string, string?), string>();
            var endpoints = new List<object>();
            foreach (var (path, type, content) in Files)
            {
                var route = path.TrimStart('/');
                var plain = Encoding.UTF8.GetBytes(content);
                foreach (var (encoding, suffix, bytes) in new[]
                {
                    ("br", ".br", Compress(plain, s => new BrotliStream(s, CompressionLevel.SmallestSize))),
                    ("gzip", ".gz", Compress(plain, s => new GZipStream(s, CompressionLevel.SmallestSize))),
                    ((string?)null, string.Empty, plain),
                })
                {
                    var file = Path.Combine(root, (route + suffix).Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                    await File.WriteAllBytesAsync(file, bytes, Ct);
                    var etag = "\"" + Convert.ToBase64String(SHA256.HashData(bytes)) + "\"";
                    etags[(path, encoding)] = etag;

                    // What the SDK writes for a file it has not fingerprinted itself: no-cache, a strong ETag per
                    // encoding, and a Content-Encoding selector for each compressed copy.
                    var headers = new List<object>
                    {
                        Header("Cache-Control", "no-cache"),
                        Header("Content-Length", bytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                        Header("Content-Type", type),
                        Header("ETag", etag),
                        Header("Last-Modified", "Wed, 04 Nov 2026 07:00:00 GMT"),
                        Header("Vary", "Accept-Encoding"),
                    };
                    if (encoding is not null)
                    {
                        headers.Add(Header("Content-Encoding", encoding));
                    }

                    endpoints.Add(new
                    {
                        Route = route,
                        AssetFile = route + suffix,
                        Selectors = encoding is null ? [] : new object[] { new { Name = "Content-Encoding", Value = encoding, Quality = Quality(bytes) } },
                        ResponseHeaders = headers,
                        EndpointProperties = Array.Empty<object>(),
                    });
                }
            }

            var manifest = Path.Combine(root, "TickPulse.Api.staticwebassets.endpoints.json");
            await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(new { Version = 1, ManifestType = "Publish", Endpoints = endpoints }), Ct);

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = "Production",
                ContentRootPath = root,
                WebRootPath = root,
            });
            builder.WebHost.UseTestServer();
            var app = builder.Build();
            app.UseDashboardAssetCaching();
            app.UseRouting();
            app.MapDashboardAssets(manifest);
            await app.StartAsync(Ct);
            return new StaticAssetsHost(app, root, etags);

            static object Header(string name, string value) => new { Name = name, Value = value };

            // As the SDK computes it: the smaller copy has the higher quality, so Brotli wins when both are accepted.
            static string Quality(byte[] bytes) => (1d / (bytes.Length + 1)).ToString("0.############", System.Globalization.CultureInfo.InvariantCulture);
        }

        public string ETag(string path, string? encoding) => _etags[(path, encoding)];

        public static string Content(string path) => Files.Single(f => f.Path == path).Content;

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.DisposeAsync();
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // A temp directory left behind is harmless.
            }
        }

        private static byte[] Compress(byte[] plain, Func<Stream, Stream> compressor)
        {
            using var output = new MemoryStream();
            using (var stream = compressor(output))
            {
                stream.Write(plain);
            }

            return output.ToArray();
        }
    }
}
