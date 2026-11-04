using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;
using OpenTelemetry.Trace;
using TickPulse.Api.Telemetry;
using TickPulse.Infrastructure.Telemetry;

namespace TickPulse.Integration.Tests.Telemetry;

public sealed class SamplerTests
{
    private const string RequestSpan = "Microsoft.AspNetCore.Hosting.HttpRequestIn";

    private static ActivityContext NoParent => default;

    [Theory]
    [InlineData(TickPulseMetrics.BackfillPageSpan, ActivityKind.Internal, SamplingDecision.RecordAndSample)]
    [InlineData(TickPulseMetrics.GapFillSpan, ActivityKind.Internal, SamplingDecision.RecordAndSample)]
    [InlineData("postgresql", ActivityKind.Client, SamplingDecision.Drop)] // e.g. the leader lock probe
    [InlineData("System.Net.Http.HttpRequestOut", ActivityKind.Client, SamplingDecision.Drop)]
    [InlineData("anything.else", ActivityKind.Internal, SamplingDecision.Drop)]
    public void Root_KeepsBackfillAndGapFill_DropsOtherRoots(string name, ActivityKind kind, SamplingDecision expected)
    {
        var sampler = TickPulseSampler.Create(flushRatio: 0, Budget(0));

        Decide(sampler, NoParent, name, kind).ShouldBe(expected);
    }

    [Fact]
    public void Request_WithinTheBudget_IsSampled_BeyondItOnlyRecorded()
    {
        var sampler = TickPulseSampler.Create(flushRatio: 0, Budget(3));

        var decisions = Enumerable.Range(0, 5).Select(_ => Decide(sampler, NoParent, RequestSpan, ActivityKind.Server)).ToList();

        // Recorded rather than dropped, so the request log can still keep the trace of an error or a slow request.
        decisions.ShouldBe(
        [
            SamplingDecision.RecordAndSample,
            SamplingDecision.RecordAndSample,
            SamplingDecision.RecordAndSample,
            SamplingDecision.RecordOnly,
            SamplingDecision.RecordOnly,
        ]);
    }

    [Fact]
    public void OnlyRequests_TakeFromTheBudget()
    {
        var budget = Budget(1);
        var sampler = TickPulseSampler.Create(flushRatio: 1, budget);

        Decide(sampler, NoParent, TickPulseMetrics.FlushSpan, ActivityKind.Internal).ShouldBe(SamplingDecision.RecordAndSample);
        Decide(sampler, NoParent, TickPulseMetrics.BackfillPageSpan, ActivityKind.Internal).ShouldBe(SamplingDecision.RecordAndSample);

        budget.TryTake().ShouldBeTrue();
    }

    [Theory]
    [InlineData(0d, 0)]
    [InlineData(1d, 1000)]
    public void RootFlush_FollowsTheRatio_AtTheBounds(double ratio, int kept)
    {
        var sampler = TickPulseSampler.Create(ratio, Budget(0));

        KeptFlushes(sampler, 1000).ShouldBe(kept);
    }

    [Fact]
    public void RootFlush_DefaultRatio_KeepsAboutOneInTen()
    {
        var sampler = TickPulseSampler.Create(TickPulseSampler.DefaultFlushRatio, Budget(0));

        KeptFlushes(sampler, 10_000).ShouldBeInRange(800, 1200);
    }

    [Theory]
    [InlineData(ActivityTraceFlags.Recorded, SamplingDecision.RecordAndSample)]
    [InlineData(ActivityTraceFlags.None, SamplingDecision.Drop)]
    public void LocalChild_FollowsItsParent(ActivityTraceFlags parentFlags, SamplingDecision expected)
    {
        var sampler = TickPulseSampler.Create(flushRatio: 0, Budget(0));
        var parent = new ActivityContext(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), parentFlags, isRemote: false);

        // A database command inside a kept request, or inside a request or flush that was not kept.
        Decide(sampler, parent, "postgresql", ActivityKind.Client).ShouldBe(expected);
        Decide(sampler, parent, TickPulseMetrics.FlushSpan, ActivityKind.Internal).ShouldBe(expected);
    }

    [Theory]
    [InlineData(ActivityTraceFlags.Recorded)]
    [InlineData(ActivityTraceFlags.None)]
    public void ServerSpan_WithRemoteParent_GoesThroughTheBudget(ActivityTraceFlags parentFlags)
    {
        var sampler = TickPulseSampler.Create(flushRatio: 0, Budget(1));
        var parent = new ActivityContext(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), parentFlags, isRemote: true);

        // A caller's sampled flag neither forces a trace nor suppresses one.
        Decide(sampler, parent, RequestSpan, ActivityKind.Server).ShouldBe(SamplingDecision.RecordAndSample);
        Decide(sampler, parent, RequestSpan, ActivityKind.Server).ShouldBe(SamplingDecision.RecordOnly);
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

    // A frozen clock: the budget never refills, so each test sees exactly perSecond sampled requests.
    private static RequestSampleBudget Budget(double perSecond) => new(perSecond, new FakeTimeProvider());

    private static IConfiguration Config(string? ratio) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Otel:FlushSampleRatio"] = ratio }).Build();

    private static SamplingDecision Decide(Sampler sampler, ActivityContext parent, string name, ActivityKind kind) =>
        sampler.ShouldSample(new SamplingParameters(parent, parent == default ? ActivityTraceId.CreateRandom() : parent.TraceId, name, kind)).Decision;

    private static int KeptFlushes(Sampler sampler, int count) =>
        Enumerable.Range(0, count).Count(_ => Decide(sampler, NoParent, TickPulseMetrics.FlushSpan, ActivityKind.Internal) == SamplingDecision.RecordAndSample);
}
