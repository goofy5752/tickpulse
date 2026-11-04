using System.Diagnostics;
using OpenTelemetry.Trace;
using TickPulse.Infrastructure.Telemetry;

namespace TickPulse.Api.Telemetry;

/// <summary>
/// Decides which traces start. HTTP requests, backfill pages and gap-fills are always kept; batch flushes are kept at
/// <c>Otel:FlushSampleRatio</c> (default <see cref="DefaultFlushRatio"/>); every other span without a parent (for
/// example the leader lock probe every few seconds) is dropped. A child span follows its parent.
/// </summary>
/// <remarks>
/// <para>Use <see cref="Create"/>, which wraps this root sampler in a <see cref="ParentBasedSampler"/>. A server span
/// whose caller sent an unsampled <c>traceparent</c> still goes through the root decision, so every request is traced
/// whatever the client sends.</para>
/// <para>Batch flushes are sampled by trace id (<see cref="TraceIdRatioBasedSampler"/>): at the default ratio about two
/// flushes per second while streaming become one kept trace every five seconds.</para>
/// </remarks>
internal sealed class TickPulseSampler : Sampler
{
    /// <summary>Configuration key of the share of <c>ingest.flush</c> traces kept, 0 to 1.</summary>
    public const string FlushRatioKey = "Otel:FlushSampleRatio";

    public const double DefaultFlushRatio = 0.1;

    private static readonly SamplingResult Keep = new(SamplingDecision.RecordAndSample);
    private static readonly SamplingResult Drop = new(SamplingDecision.Drop);

    private readonly TraceIdRatioBasedSampler _flush;

    public TickPulseSampler(double flushRatio)
    {
        _flush = new TraceIdRatioBasedSampler(flushRatio);
        Description = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"TickPulseSampler{{flush={flushRatio}}}");
    }

    /// <summary>The sampler of the trace pipeline: parent-based around <see cref="TickPulseSampler"/>.</summary>
    public static Sampler Create(double flushRatio)
    {
        var root = new TickPulseSampler(flushRatio);
        return new ParentBasedSampler(
            root,
            remoteParentSampled: new AlwaysOnSampler(),
            remoteParentNotSampled: root,
            localParentSampled: new AlwaysOnSampler(),
            localParentNotSampled: new AlwaysOffSampler());
    }

    public override SamplingResult ShouldSample(in SamplingParameters samplingParameters)
    {
        var name = samplingParameters.Name;
        if (name == TickPulseMetrics.FlushSpan)
        {
            return _flush.ShouldSample(samplingParameters);
        }

        return samplingParameters.Kind == ActivityKind.Server
            || name == TickPulseMetrics.BackfillPageSpan
            || name == TickPulseMetrics.GapFillSpan
            ? Keep
            : Drop;
    }
}
