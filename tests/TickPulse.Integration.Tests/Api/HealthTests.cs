using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using TickPulse.Api.Hosting;
using TickPulse.Application.Ingestion;
using TickPulse.Infrastructure.Ingestion;
using TickPulse.Integration.Tests.Fixtures;
using TickPulse.Integration.Tests.Ingestion;
using static TickPulse.Integration.Tests.Api.ProblemReader;

namespace TickPulse.Integration.Tests.Api;

public sealed class HealthTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task HealthLive_Returns200()
    {
        using var client = api.CreateClient();

        using var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task HealthReady_DbUp_Returns200()
    {
        using var client = api.CreateClient();

        using var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        MediaType(response).ShouldBe("application/json");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        body.RootElement.GetProperty("status").GetString().ShouldBe("Healthy");
        var checks = body.RootElement.GetProperty("checks").EnumerateArray()
            .ToDictionary(c => c.GetProperty("name").GetString()!, c => c.GetProperty("status").GetString());
        checks.ShouldBe(new Dictionary<string, string?> { ["database"] = "Healthy", ["ingestion"] = "Healthy" }, ignoreOrder: true);
        foreach (var check in body.RootElement.GetProperty("checks").EnumerateArray())
        {
            check.GetProperty("description").GetString().ShouldNotBeNullOrWhiteSpace();
        }
    }

    // Connecting and waiting to reconnect are expected, self-healing states: not ready, but Degraded rather than
    // Unhealthy, so the health check log reports them at Warning instead of Error.
    [Theory]
    [InlineData(WsState.Connecting)]
    [InlineData(WsState.Disconnected)]
    public async Task HealthReady_IngestionNotStreaming_Returns503Degraded(WsState notStreaming)
    {
        var state = api.Services.GetRequiredService<IngestionState>();
        using var client = api.CreateClient();
        if (notStreaming == WsState.Connecting)
        {
            state.SetConnecting();
        }
        else
        {
            state.SetDisconnected();
        }

        try
        {
            using var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), Ct);

            response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
            body.RootElement.GetProperty("status").GetString().ShouldBe("Degraded");
            var ingestion = Check(body, "ingestion");
            ingestion.GetProperty("status").GetString().ShouldBe("Degraded");
            ingestion.TryGetProperty("description", out _).ShouldBeFalse();
        }
        finally
        {
            state.SetStandby();
        }
    }

    [Fact]
    public async Task HealthReady_DbDown_Returns503_LiveStays200()
    {
        await using var down = new ApiFactory(api.Database)
            .WithSetting("ConnectionStrings:TickPulse", "Host=127.0.0.1;Port=1;Database=tickpulse;Username=x;Password=x;Timeout=2")
            .WithSetting("Database:RunMigrationsOnStartup", "false");
        await down.InitializeAsync();
        using var client = down.CreateClient();

        using var ready = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), Ct);
        using var live = await client.GetAsync(new Uri("/health/live", UriKind.Relative), Ct);

        ready.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        var raw = await ready.Content.ReadAsStringAsync(Ct);
        using var body = JsonDocument.Parse(raw);
        var database = Check(body, "database");
        database.GetProperty("status").GetString().ShouldBe("Unhealthy");
        database.TryGetProperty("description", out _).ShouldBeFalse();

        // Nothing from the exception (Npgsql: "Failed to connect to 127.0.0.1:1") reaches the response.
        raw.ShouldNotContain("127.0.0.1");
        raw.ShouldNotContain("connect", Case.Insensitive);
        live.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static JsonElement Check(JsonDocument report, string name) =>
        report.RootElement.GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("name").GetString() == name);

    [Fact]
    public async Task HealthReady_WithWorkers_ConnectsAfterMigrations()
    {
        await using var full = new ApiFactory(api.Database).WithWorkers().WithSystemClock();
        await full.InitializeAsync();
        using var client = full.CreateClient();
        var state = full.Services.GetRequiredService<IngestionState>();

        await IngestionTestHost.WaitForAsync(() => state.Status == WsState.Connected, "the ingestion worker to connect");
        using var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var hosted = full.Services.GetServices<IHostedService>().Select(s => s.GetType()).ToList();
        hosted.IndexOf(typeof(DatabaseMigrator)).ShouldBe(0);
        hosted.ShouldContain(typeof(IngestionWorker));
    }

    [Fact]
    public async Task Startup_FreshDatabase_AppliesMigrations()
    {
        var name = "tickpulse_migrate_" + Guid.NewGuid().ToString("N")[..8];
        await ExecuteAsync(api.Database.ConnectionString, $"CREATE DATABASE {name}");
        var connectionString = new NpgsqlConnectionStringBuilder(api.Database.ConnectionString) { Database = name, Pooling = false }.ConnectionString;
        try
        {
            await using (var fresh = new ApiFactory(api.Database).WithSetting("ConnectionStrings:TickPulse", connectionString))
            {
                await fresh.InitializeAsync();
                using var client = fresh.CreateClient();
                using var ready = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), Ct);
                ready.StatusCode.ShouldBe(HttpStatusCode.OK);
            }

            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(Ct);
            await using var command = new NpgsqlCommand("SELECT string_agg(name, ',' ORDER BY name) FROM symbols", connection);
            (await command.ExecuteScalarAsync(Ct)).ShouldBe("ADAUSDT,BTCUSDT,ETHUSDT");
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await ExecuteAsync(api.Database.ConnectionString, $"DROP DATABASE IF EXISTS {name} WITH (FORCE)");
        }
    }

    [Fact]
    public async Task Docs_ServesScalar()
    {
        using var client = api.CreateClient();

        using var response = await client.GetAsync(new Uri("/docs", UriKind.Relative), Ct);

        // Scalar redirects to /docs/ and resolves the relative source against the site root: /openapi/v1.json.
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.RequestMessage!.RequestUri!.AbsolutePath.ShouldBe("/docs/");
        MediaType(response).ShouldBe("text/html");
        var html = await response.Content.ReadAsStringAsync(Ct);
        html.ShouldContain("<title>TickPulse API</title>");
        html.ShouldContain("\"url\":\"openapi/v1.json\"");
        using var script = await client.GetAsync(new Uri("/docs/scalar.js", UriKind.Relative), Ct);
        script.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task OpenApiDocument_Returns200()
    {
        using var client = api.CreateClient();

        using var response = await client.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        document.RootElement.GetProperty("info").GetProperty("title").GetString().ShouldBe("TickPulse API");
    }

    [Fact]
    public async Task Root_ServesIndexHtml_WhenPresent()
    {
        const string marker = "<!doctype html><title>tickpulse-test-dashboard</title>";
        var index = Path.Combine(api.WebRoot, "index.html");
        await File.WriteAllTextAsync(index, marker, Ct);
        try
        {
            using var client = api.CreateClient();

            foreach (var path in new[] { "/", "/symbols/BTCUSDT" })
            {
                using var page = await client.GetAsync(new Uri(path, UriKind.Relative), Ct);
                page.StatusCode.ShouldBe(HttpStatusCode.OK, path);
                MediaType(page).ShouldBe("text/html", path);
                (await page.Content.ReadAsStringAsync(Ct)).ShouldBe(marker, path);
            }

            // Reserved prefixes never fall back to the dashboard.
            foreach (var path in new[] { "/api/does/not/exist", "/api", "/hubs/nope", "/health/nope" })
            {
                using var miss = await client.GetAsync(new Uri(path, UriKind.Relative), Ct);
                miss.StatusCode.ShouldBe(HttpStatusCode.NotFound, path);
                MediaType(miss).ShouldBe(JsonMediaType, path);
            }
        }
        finally
        {
            File.Delete(index);
        }
    }

    [Fact]
    public async Task Root_WithoutIndexHtml_Returns404Problem()
    {
        using var client = api.CreateClient();

        using var response = await client.GetAsync(new Uri("/", UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        MediaType(response).ShouldBe(JsonMediaType);
    }

    [Theory]
    [InlineData("/api/probe/BTCUSDT")]
    [InlineData("/api/does/not/exist")]
    [InlineData("/health/live")]
    public async Task Responses_CarrySecurityHeaders(string path)
    {
        using var client = api.CreateClient();

        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), Ct);

        response.Headers.GetValues("X-Content-Type-Options").ShouldBe(["nosniff"]);
        response.Headers.GetValues("Referrer-Policy").ShouldBe(["no-referrer"]);
        response.Headers.GetValues("X-Frame-Options").ShouldBe(["DENY"]);
    }

    // connect-src 'self' also covers the hub's same-origin ws:/wss: connection (CSP Level 3).
    private const string StrictPolicy =
        "default-src 'self'; connect-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; " +
        "script-src 'self'; font-src 'self'; object-src 'none'; frame-ancestors 'none'; base-uri 'self'; " +
        "form-action 'self'";

    [Theory]
    [InlineData("/")]
    [InlineData("/symbols/BTCUSDT")]
    [InlineData("/favicon.svg")]
    [InlineData("/api/probe/BTCUSDT")]
    [InlineData("/api/does/not/exist")]
    [InlineData("/health/live")]
    [InlineData("/openapi/v1.json")]
    [InlineData("/docsx")]
    public async Task DashboardAndApiResponses_CarryTheStrictContentSecurityPolicy(string path)
    {
        var index = Path.Combine(api.WebRoot, "index.html");
        var favicon = Path.Combine(api.WebRoot, "favicon.svg");
        await File.WriteAllTextAsync(index, "<!doctype html><title>csp</title>", Ct);
        await File.WriteAllTextAsync(favicon, "<svg xmlns=\"http://www.w3.org/2000/svg\"/>", Ct);
        try
        {
            using var client = api.CreateClient();

            using var response = await client.GetAsync(new Uri(path, UriKind.Relative), Ct);

            response.Headers.GetValues("Content-Security-Policy").ShouldBe([StrictPolicy], path);
        }
        finally
        {
            File.Delete(index);
            File.Delete(favicon);
        }
    }

    [Theory]
    [InlineData("/docs")]
    [InlineData("/docs/")]
    [InlineData("/docs/scalar.js")]
    public async Task Docs_CarryTheirOwnSelfHostedContentSecurityPolicy(string path)
    {
        using var client = new HttpClient(api.Server.CreateHandler()) { BaseAddress = api.Server.BaseAddress };

        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), Ct);

        // Scalar's page boots from one inline script, so only /docs allows inline scripts; every source is still
        // this origin, and framing, <base> and form targets stay locked down.
        response.Headers.GetValues("Content-Security-Policy").ShouldBe(
        [
            "default-src 'self'; connect-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; " +
            "script-src 'self' 'unsafe-inline'; font-src 'self'; object-src 'none'; frame-ancestors 'none'; " +
            "base-uri 'self'; form-action 'self'",
        ], path);
    }

    [Fact]
    public async Task Docs_LoadNothingFromOtherOrigins()
    {
        using var client = api.CreateClient();

        var html = await client.GetStringAsync(new Uri("/docs/", UriKind.Relative), Ct);

        // No CDN fonts, telemetry or AI agent: the page needs nothing outside this origin.
        html.ShouldNotContain("fonts.scalar.com");
        html.ShouldContain("\"withDefaultFonts\":false");
        html.ShouldContain("\"telemetry\":false");
        html.ShouldContain("\"agent\":{\"disabled\":true}");
    }

    [Fact]
    public async Task Docs_SwitchZodToJitlessBeforeScalarLoads()
    {
        using var client = api.CreateClient();

        var html = await client.GetStringAsync(new Uri("/docs/", UriKind.Relative), Ct);

        // Scalar bundles Zod, whose first schema probes eval with Function(""). The policy has no 'unsafe-eval', so
        // the probe would be reported as a violation; jitless mode skips it. It must run before scalar.js.
        var config = html.IndexOf("globalThis.__zod_globalConfig = { jitless: true }", StringComparison.Ordinal);
        config.ShouldBeGreaterThan(0);
        config.ShouldBeLessThan(html.IndexOf("<script src=\"scalar.js\"></script>", StringComparison.Ordinal));
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Ct);
    }
}
