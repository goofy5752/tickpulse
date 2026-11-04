using TickPulse.Integration.Tests.Fixtures;

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
