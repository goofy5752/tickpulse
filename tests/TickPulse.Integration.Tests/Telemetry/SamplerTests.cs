using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using OpenTelemetry.Trace;
using TickPulse.Api.Telemetry;
using TickPulse.Infrastructure.Telemetry;

namespace TickPulse.Integration.Tests.Telemetry;

public sealed class SamplerTests
{
    private static ActivityContext NoParent => default;

    [Theory]
    [InlineData("Microsoft.AspNetCore.Hosting.HttpRequestIn", ActivityKind.Server, SamplingDecision.RecordAndSample)]
    [InlineData(TickPulseMetrics.BackfillPageSpan, ActivityKind.Internal, SamplingDecision.RecordAndSample)]
    [InlineData(TickPulseMetrics.GapFillSpan, ActivityKind.Internal, SamplingDecision.RecordAndSample)]
    [InlineData("postgresql", ActivityKind.Client, SamplingDecision.Drop)] // e.g. the leader lock probe
    [InlineData("System.Net.Http.HttpRequestOut", ActivityKind.Client, SamplingDecision.Drop)]
    [InlineData("anything.else", ActivityKind.Internal, SamplingDecision.Drop)]
    public void Root_KeepsRequestsBackfillAndGapFill_DropsOtherRoots(string name, ActivityKind kind, SamplingDecision expected)
    {
        var sampler = TickPulseSampler.Create(flushRatio: 0);

        Decide(sampler, NoParent, name, kind).ShouldBe(expected);
    }

    [Theory]
    [InlineData(0d, 0)]
    [InlineData(1d, 1000)]
    public void RootFlush_FollowsTheRatio_AtTheBounds(double ratio, int kept)
    {
        var sampler = TickPulseSampler.Create(ratio);

        KeptFlushes(sampler, 1000).ShouldBe(kept);
    }

    [Fact]
    public void RootFlush_DefaultRatio_KeepsAboutOneInTen()
    {
        var sampler = TickPulseSampler.Create(TickPulseSampler.DefaultFlushRatio);

        KeptFlushes(sampler, 10_000).ShouldBeInRange(800, 1200);
    }

    [Theory]
    [InlineData(ActivityTraceFlags.Recorded, SamplingDecision.RecordAndSample)]
    [InlineData(ActivityTraceFlags.None, SamplingDecision.Drop)]
    public void LocalChild_FollowsItsParent(ActivityTraceFlags parentFlags, SamplingDecision expected)
    {
        var sampler = TickPulseSampler.Create(flushRatio: 0);
        var parent = new ActivityContext(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), parentFlags, isRemote: false);

        // A database command inside a kept request, or inside a flush that was not kept.
        Decide(sampler, parent, "postgresql", ActivityKind.Client).ShouldBe(expected);
        Decide(sampler, parent, TickPulseMetrics.FlushSpan, ActivityKind.Internal).ShouldBe(expected);
    }

    [Theory]
    [InlineData(ActivityTraceFlags.Recorded)]
    [InlineData(ActivityTraceFlags.None)]
    public void ServerSpan_WithRemoteParent_IsAlwaysKept(ActivityTraceFlags parentFlags)
    {
        var sampler = TickPulseSampler.Create(flushRatio: 0);
        var parent = new ActivityContext(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), parentFlags, isRemote: true);

        Decide(sampler, parent, "Microsoft.AspNetCore.Hosting.HttpRequestIn", ActivityKind.Server).ShouldBe(SamplingDecision.RecordAndSample);
    }

    [Theory]
    [InlineData(null, 0.1)]
    [InlineData("", 0.1)]
    [InlineData("0.25", 0.25)]
    [InlineData("0", 0d)]
    [InlineData("1", 1d)]
    public void FlushSampleRatio_ReadsTheSetting(string? value, double expected) =>
        TelemetrySetup.FlushSampleRatio(Config(value)).ShouldBe(expected);

    [Theory]
    [InlineData("1.5")]
    [InlineData("-0.1")]
    [InlineData("ten percent")]
    public void FlushSampleRatio_Invalid_Throws(string value) =>
        Should.Throw<InvalidOperationException>(() => TelemetrySetup.FlushSampleRatio(Config(value))).Message.ShouldContain("Otel:FlushSampleRatio");

    private static IConfiguration Config(string? ratio) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Otel:FlushSampleRatio"] = ratio }).Build();

    private static SamplingDecision Decide(Sampler sampler, ActivityContext parent, string name, ActivityKind kind) =>
        sampler.ShouldSample(new SamplingParameters(parent, parent == default ? ActivityTraceId.CreateRandom() : parent.TraceId, name, kind)).Decision;

    private static int KeptFlushes(Sampler sampler, int count) =>
        Enumerable.Range(0, count).Count(_ => Decide(sampler, NoParent, TickPulseMetrics.FlushSpan, ActivityKind.Internal) == SamplingDecision.RecordAndSample);
}
