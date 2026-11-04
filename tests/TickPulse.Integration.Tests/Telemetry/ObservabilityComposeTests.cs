using TickPulse.Integration.Tests.Fixtures;
using YamlDotNet.RepresentationModel;

namespace TickPulse.Integration.Tests.Telemetry;

/// <summary>
/// The observability services get time to stop: without a <c>stop_grace_period</c>, Compose kills a container 1 s after
/// SIGTERM, and Tempo, Loki and Prometheus lose what they buffered or have to replay their WAL after every redeploy.
/// </summary>
public sealed class ObservabilityComposeTests
{
    public const string Observability = "deploy/observability/compose.observability.yml";

    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(30);

    public static TheoryData<string> Services => ["otel-collector", "prometheus", "loki", "tempo", "grafana"];

    [Theory]
    [MemberData(nameof(Services))]
    public void ObservabilityService_HasAStopGracePeriod(string service)
    {
        ComposeFile.Load(Observability).StopGracePeriod(service)
            .ShouldBe(Grace, $"{Observability}: {service} needs a stop_grace_period (Compose otherwise kills it after 1 s)");
    }

    [Fact]
    public void Tempo_StopsWellInsideItsGracePeriod()
    {
        // On SIGTERM Tempo's backend worker waits finish_on_shutdown_timeout for its current job, even with none
        // running. The default, 30 s, is the whole grace period: Compose then killed Tempo all the same.
        using var reader = new StreamReader(Path.Combine(ComposeFile.Root, "deploy", "observability", "tempo", "tempo.yaml"));
        var yaml = new YamlStream();
        yaml.Load(reader);
        var root = (YamlMappingNode)yaml.Documents.Single().RootNode;
        var worker = (YamlMappingNode)root.Children[new YamlScalarNode("backend_worker")];
        worker.Children.TryGetValue(new YamlScalarNode("finish_on_shutdown_timeout"), out var timeout)
            .ShouldBeTrue("tempo.yaml: backend_worker.finish_on_shutdown_timeout is unset (default 30 s, the whole grace period)");

        ComposeFile.ParseDuration(((YamlScalarNode)timeout!).Value!).ShouldBeLessThanOrEqualTo(Grace / 2);
    }

    [Theory]
    [MemberData(nameof(Services))]
    public void Production_KeepsTheStopGracePeriodItExtends(string service)
    {
        // The production services extend the ones above; one that set its own grace period could only shorten it.
        var own = ComposeFile.Load(ComposeFile.Production).StopGracePeriod(service);
        if (own is { } grace)
        {
            grace.ShouldBeGreaterThanOrEqualTo(Grace, $"{ComposeFile.Production}: {service}");
        }
    }
}
