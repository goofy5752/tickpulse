using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace TickPulse.Integration.Tests.Telemetry;

/// <summary>
/// Dashboard queries evaluated by a real Prometheus (the image of <c>compose.observability.yml</c>) over counter
/// series shaped like the ones TickPulse exports: one series per process (<c>service_instance_id</c>), with no zero
/// sample at the start, because the first export already carries everything counted since the process started.
/// </summary>
public sealed partial class DashboardQueryTests : IAsyncLifetime
{
    private const int PrometheusPort = 9090;
    private const string Range = "10m";

    private IContainer? _prometheus;
    private HttpClient? _client;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private HttpClient Client => _client ?? throw new InvalidOperationException("Not initialised.");

    public async ValueTask InitializeAsync()
    {
        _prometheus = new ContainerBuilder(PrometheusImage())
            .WithCommand("--config.file=/etc/prometheus/prometheus.yml", "--storage.tsdb.path=/prometheus", "--web.enable-otlp-receiver")
            .WithPortBinding(PrometheusPort, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(PrometheusPort).ForPath("/-/ready")))
            .Build();
        await _prometheus.StartAsync(Ct);
        _client = new HttpClient
        {
            BaseAddress = new Uri($"http://{_prometheus.Hostname}:{_prometheus.GetMappedPublicPort(PrometheusPort)}"),
        };
    }

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        if (_prometheus is not null)
        {
            await _prometheus.DisposeAsync();
        }
    }

    [Fact]
    public async Task CandlesBackfilled_CountsEveryCandleStoredInTheRange()
    {
        // Whole seconds, so that the sample times and the evaluation time line up exactly.
        var end = DateTimeOffset.FromUnixTimeSeconds(TimeProvider.System.GetUtcNow().ToUnixTimeSeconds() - 30);

        await PushAsync(
            // BTCUSDT, process p0: ran and stopped before the range; none of it counts.
            Series("BTCUSDT", "p0", end, (-20, 1_000), (-15, 2_000), (-12, 2_500)),
            // BTCUSDT, process p1: started before the range; it stored 20 000 − 8 000 = 12 000 inside it.
            Series("BTCUSDT", "p1", end, (-14, 5_000), (-10, 8_000), (-5, 15_000), (0, 20_000)),
            // ADAUSDT, process p1: started inside the range. Its first export is already 3 000, and all 4 000 count.
            Series("ADAUSDT", "p1", end, (-5, 3_000), (-3, 3_500), (0, 4_000)));

        var counted = await QueryAsync(PanelExpression("ingestion.json", "Candles backfilled"), end);

        counted.ShouldBe(new Dictionary<string, decimal> { ["BTCUSDT"] = 12_000m, ["ADAUSDT"] = 4_000m });
    }

    [Theory]
    [InlineData(10, 10, -1, 0)] // idle: every request in flight is a dashboard's live hub WebSocket
    [InlineData(12, 10, -1, 2)]
    [InlineData(3, -1, -1, 3)] // no hub connection series at all
    [InlineData(0, 1, -1, 0)] // a transport counted a moment before its request: never negative
    [InlineData(5, 3, 4, 2)] // long polling holds no request open between polls: only WebSockets are subtracted
    [InlineData(2, -1, 4, 2)]
    public async Task InFlight_LeavesOutTheLiveHubConnections(long activeRequests, long webSockets, long longPolling, long expected)
    {
        var end = DateTimeOffset.FromUnixTimeSeconds(TimeProvider.System.GetUtcNow().ToUnixTimeSeconds() - 30);
        await PushGaugeAsync("http.server.active_requests", "{request}", "Microsoft.AspNetCore.Hosting", end, activeRequests);
        foreach (var (transport, connections) in new[] { ("web_sockets", webSockets), ("long_polling", longPolling) })
        {
            if (connections >= 0)
            {
                await PushGaugeAsync("signalr.server.active_connections", "{connection}", "Microsoft.AspNetCore.Http.Connections", end, connections, transport);
            }
        }

        var inFlight = await QueryScalarAsync(PanelExpression("api.json", "In-flight"), end);

        inFlight.ShouldBe(expected);
    }

    /// <summary>Sends one value of an up-down counter (a non-monotonic cumulative OTLP sum, a Prometheus gauge).</summary>
    private async Task PushGaugeAsync(string name, string unit, string scope, DateTimeOffset at, long value, string? transport = null)
    {
        var request = new JsonObject
        {
            ["resourceMetrics"] = new JsonArray(new JsonObject
            {
                ["resource"] = new JsonObject { ["attributes"] = new JsonArray() },
                ["scopeMetrics"] = new JsonArray(new JsonObject
                {
                    ["scope"] = new JsonObject { ["name"] = scope },
                    ["metrics"] = new JsonArray(new JsonObject
                    {
                        ["name"] = name,
                        ["unit"] = unit,
                        ["sum"] = new JsonObject
                        {
                            ["aggregationTemporality"] = 2,
                            ["isMonotonic"] = false,
                            ["dataPoints"] = new JsonArray(new JsonObject
                            {
                                ["attributes"] = transport is null
                                    ? Attributes(("service_name", "tickpulse-api"), ("service_instance_id", "p1"))
                                    : Attributes(("service_name", "tickpulse-api"), ("service_instance_id", "p1"), ("signalr.transport", transport)),
                                ["timeUnixNano"] = (at.ToUnixTimeMilliseconds() * 1_000_000).ToString(CultureInfo.InvariantCulture),
                                ["asInt"] = value.ToString(CultureInfo.InvariantCulture),
                            }),
                        },
                    }),
                }),
            }),
        };

        using var content = new StringContent(request.ToJsonString(), Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await Client.PostAsync(new Uri("/api/v1/otlp/v1/metrics", UriKind.Relative), content, Ct);
        response.IsSuccessStatusCode.ShouldBeTrue(await response.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>An instant query at <paramref name="at"/> that yields one unlabelled value.</summary>
    private async Task<decimal> QueryScalarAsync(string expression, DateTimeOffset at)
    {
        var uri = $"/api/v1/query?query={Uri.EscapeDataString(expression)}&time={at.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}";
        using var response = await Client.GetAsync(new Uri(uri, UriKind.Relative), Ct);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        response.IsSuccessStatusCode.ShouldBeTrue(body.ToString());
        var result = body.GetProperty("data").GetProperty("result").EnumerateArray().ShouldHaveSingleItem(body.ToString());
        return decimal.Parse(result.GetProperty("value")[1].GetString()!, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    private static string PrometheusImage()
    {
        var compose = File.ReadAllText(Path.Combine(RepositoryRoot(), "deploy", "observability", "compose.observability.yml"));
        return PrometheusImageLine().Match(compose) is { Success: true } match
            ? match.Groups[1].Value
            : throw new InvalidOperationException("No prom/prometheus image in compose.observability.yml.");
    }

    /// <summary>The expression of the panel titled <paramref name="title"/>, with the dashboard variables filled in.</summary>
    private static string PanelExpression(string dashboard, string title)
    {
        var path = Path.Combine(RepositoryRoot(), "deploy", "observability", "grafana", "dashboards", dashboard);
        var root = JsonNode.Parse(File.ReadAllText(path)) ?? throw new InvalidOperationException(path);
        var panel = Panels(root["panels"]!.AsArray()).Single(p => (string?)p["title"] == title);
        var expression = (string)panel["targets"]!.AsArray().Single()!["expr"]!;
        return expression.Replace("$__range", Range, StringComparison.Ordinal).Replace("$symbol", ".*", StringComparison.Ordinal);

        static IEnumerable<JsonNode> Panels(JsonArray panels) =>
            panels.OfType<JsonNode>().SelectMany(p => p["panels"] is JsonArray nested ? Panels(nested).Prepend(p) : [p]);
    }

    private static JsonObject Series(string symbol, string instance, DateTimeOffset end, params (int Minutes, long Value)[] points) => new()
    {
        ["attributes"] = Attributes(("symbol", symbol), ("service_name", "tickpulse-api"), ("service_instance_id", instance)),
        ["points"] = new JsonArray([.. points.Select(p => (JsonNode)new JsonObject
        {
            ["timeUnixNano"] = (end.AddMinutes(p.Minutes).ToUnixTimeMilliseconds() * 1_000_000).ToString(CultureInfo.InvariantCulture),
            ["asInt"] = p.Value.ToString(CultureInfo.InvariantCulture),
        })]),
    };

    private static JsonArray Attributes(params (string Key, string Value)[] attributes) =>
        new([.. attributes.Select(a => (JsonNode)new JsonObject { ["key"] = a.Key, ["value"] = new JsonObject { ["stringValue"] = a.Value } })]);

    /// <summary>Sends the series as one cumulative, monotonic OTLP sum: <c>tickpulse.backfill.candles</c>.</summary>
    private async Task PushAsync(params JsonObject[] series)
    {
        var dataPoints = series.SelectMany(s => s["points"]!.AsArray().Select(p => (JsonNode)new JsonObject
        {
            ["attributes"] = s["attributes"]!.DeepClone(),
            ["timeUnixNano"] = p!["timeUnixNano"]!.DeepClone(),
            ["asInt"] = p["asInt"]!.DeepClone(),
        }));
        var request = new JsonObject
        {
            ["resourceMetrics"] = new JsonArray(new JsonObject
            {
                ["resource"] = new JsonObject { ["attributes"] = new JsonArray() },
                ["scopeMetrics"] = new JsonArray(new JsonObject
                {
                    ["scope"] = new JsonObject { ["name"] = "TickPulse" },
                    ["metrics"] = new JsonArray(new JsonObject
                    {
                        ["name"] = "tickpulse.backfill.candles",
                        ["unit"] = "{candle}",
                        ["sum"] = new JsonObject
                        {
                            ["aggregationTemporality"] = 2,
                            ["isMonotonic"] = true,
                            ["dataPoints"] = new JsonArray([.. dataPoints]),
                        },
                    }),
                }),
            }),
        };

        // Prometheus accepts exactly "application/json", without a charset parameter.
        using var content = new StringContent(request.ToJsonString(), Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await Client.PostAsync(new Uri("/api/v1/otlp/v1/metrics", UriKind.Relative), content, Ct);
        response.IsSuccessStatusCode.ShouldBeTrue(await response.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>An instant query at <paramref name="at"/>: the value of each result series by its <c>symbol</c>.</summary>
    private async Task<Dictionary<string, decimal>> QueryAsync(string expression, DateTimeOffset at)
    {
        var uri = $"/api/v1/query?query={Uri.EscapeDataString(expression)}&time={at.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}";
        using var response = await Client.GetAsync(new Uri(uri, UriKind.Relative), Ct);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        response.IsSuccessStatusCode.ShouldBeTrue(body.ToString());
        return body.GetProperty("data").GetProperty("result").EnumerateArray().ToDictionary(
            r => r.GetProperty("metric").GetProperty("symbol").GetString()!,
            r => decimal.Parse(r.GetProperty("value")[1].GetString()!, NumberStyles.Float, CultureInfo.InvariantCulture));
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TickPulse.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("TickPulse.slnx not found above the test output.");
    }

    [GeneratedRegex(@"image:\s*(prom/prometheus:\S+)")]
    private static partial Regex PrometheusImageLine();
}
