using System.Diagnostics;
using OpenTelemetry.Trace;
using TickPulse.Infrastructure.Telemetry;

namespace TickPulse.Api.Telemetry;

/// <summary>
/// Decides which traces start. HTTP requests are kept within <see cref="RequestSampleBudget"/> and only recorded
/// beyond it; backfill pages and gap-fills are always kept; batch flushes are kept at <c>Otel:FlushSampleRatio</c>
/// (default <see cref="DefaultFlushRatio"/>); every other span without a parent (for example the leader lock probe
/// every few seconds) is dropped. A child span follows its parent.
/// </summary>
/// <remarks>
/// <para>Use <see cref="Create"/>, which wraps this root sampler in a <see cref="ParentBasedSampler"/>. A server span
/// always goes through the root decision, whatever <c>traceparent</c> the caller sent, so a client cannot raise the
/// share of traced requests.</para>
/// <para>A request beyond the budget is recorded but not sampled: its server span gets its attributes but is not
/// exported, and its database spans are never created. The request log keeps the trace of an error or a slow request
/// when it completes (<see cref="RequestLogPolicy.KeepTrace"/>), so those are exported with the server span alone.</para>
/// <para>Batch flushes are sampled by trace id (<see cref="TraceIdRatioBasedSampler"/>): at the default ratio about two
/// flushes per second while streaming become one kept trace every five seconds.</para>
/// </remarks>
internal sealed class TickPulseSampler : Sampler
{
    /// <summary>Configuration key of the share of <c>ingest.flush</c> traces kept, 0 to 1.</summary>
    public const string FlushRatioKey = "Otel:FlushSampleRatio";

    public const double DefaultFlushRatio = 0.1;

    private static readonly SamplingResult Keep = new(SamplingDecision.RecordAndSample);
    private static readonly SamplingResult RecordOnly = new(SamplingDecision.RecordOnly);
    private static readonly SamplingResult Drop = new(SamplingDecision.Drop);

    private readonly TraceIdRatioBasedSampler _flush;
    private readonly RequestSampleBudget _requests;

    public TickPulseSampler(double flushRatio, RequestSampleBudget requests)
    {
        ArgumentNullException.ThrowIfNull(requests);
        _flush = new TraceIdRatioBasedSampler(flushRatio);
        _requests = requests;
        Description = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"TickPulseSampler{{flush={flushRatio}}}");
    }

    /// <summary>The sampler of the trace pipeline: parent-based around <see cref="TickPulseSampler"/>.</summary>
    public static Sampler Create(double flushRatio, RequestSampleBudget requests)
    {
        var root = new TickPulseSampler(flushRatio, requests);
        return new ParentBasedSampler(
            root,
            remoteParentSampled: root,
            remoteParentNotSampled: root,
            localParentSampled: new AlwaysOnSampler(),
            localParentNotSampled: new AlwaysOffSampler());
    }

    public override SamplingResult ShouldSample(in SamplingParameters samplingParameters)
    {
        var name = samplingParameters.Name;
        if (samplingParameters.Kind == ActivityKind.Server)
        {
            return _requests.TryTake() ? Keep : RecordOnly;
        }

        if (name == TickPulseMetrics.FlushSpan)
        {
            return _flush.ShouldSample(samplingParameters);
        }

        return name == TickPulseMetrics.BackfillPageSpan || name == TickPulseMetrics.GapFillSpan ? Keep : Drop;
    }
}
