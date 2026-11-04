using System.Diagnostics;
using System.Globalization;
using Serilog.Events;
using TickPulse.Api.Health;

namespace TickPulse.Api.Telemetry;

/// <summary>
/// How many ordinary requests per second keep their trace and their Information request log line
/// (<c>Otel:SampledRequestsPerSecond</c>, default <see cref="DefaultPerSecond"/>). A token bucket that starts full and
/// holds one second's worth, so light traffic is recorded in full and a burst of thousands of requests per second
/// still records only this many.
/// </summary>
/// <remarks>
/// Without a collector the request log alone takes from it; with one, the trace sampler takes from it when the
/// request starts and the request log follows that decision (see <see cref="RequestLogPolicy"/>).
/// </remarks>
internal sealed class RequestSampleBudget
{
    /// <summary>Configuration key of the rate, a number of requests per second, 0 or more (invariant culture).</summary>
    public const string PerSecondKey = "Otel:SampledRequestsPerSecond";

    public const double DefaultPerSecond = 10;

    private readonly double _perSecond;
    private readonly double _capacity;
    private readonly TimeProvider _clock;
    private readonly Lock _lock = new();
    private double _tokens;
    private long _refilledAt;

    /// <param name="perSecond">Requests kept per second; 0 keeps none.</param>
    /// <param name="clock">Clock that refills the bucket.</param>
    public RequestSampleBudget(double perSecond, TimeProvider clock)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(perSecond);
        ArgumentNullException.ThrowIfNull(clock);
        _perSecond = perSecond;
        _capacity = Math.Max(1, perSecond);
        _clock = clock;
        _tokens = perSecond > 0 ? _capacity : 0;
        _refilledAt = clock.GetTimestamp();
    }

    /// <summary>Takes one request from the budget; <see langword="false"/> when this second's share is used up.</summary>
    public bool TryTake()
    {
        if (_perSecond <= 0)
        {
            return false;
        }

        lock (_lock)
        {
            var now = _clock.GetTimestamp();
            _tokens = Math.Min(_capacity, _tokens + (_clock.GetElapsedTime(_refilledAt, now).TotalSeconds * _perSecond));
            _refilledAt = now;
            if (_tokens < 1)
            {
                return false;
            }

            _tokens--;
            return true;
        }
    }

    /// <summary>The configured rate, or <see cref="DefaultPerSecond"/> when unset or blank.</summary>
    /// <exception cref="InvalidOperationException">The value is not a number of 0 or more.</exception>
    public static double PerSecond(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var raw = configuration[PerSecondKey];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return DefaultPerSecond;
        }

        return double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var rate) && rate >= 0 && double.IsFinite(rate)
            ? rate
            : throw new InvalidOperationException(string.Create(
                CultureInfo.InvariantCulture,
                $"{PerSecondKey} must be a number of 0 or more, such as 10."));
    }
}

/// <summary>
/// The level of each request's log line, chosen when the request completes. Problems and slow requests are always
/// logged; ordinary requests are logged only as far as <see cref="RequestSampleBudget"/> allows, so a traffic burst
/// cannot flood the log pipeline.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>an unhandled exception, or a 5xx outside <c>/health</c>: Error;</item>
/// <item><c>/health/*</c>: Warning when not ready (503, an expected state while ingestion connects), Debug otherwise;</item>
/// <item>slower than <see cref="SlowThreshold"/>: Information;</item>
/// <item>4xx other than 429: Information;</item>
/// <item>2xx, 3xx and 429 (expected under load and already counted by the metrics): Information when sampled,
/// Debug otherwise.</item>
/// </list>
/// A request whose line is written at Information or above also keeps its trace (<see cref="KeepTrace"/>), so every
/// logged request can be opened as a trace.
/// </remarks>
internal static class RequestLogPolicy
{
    /// <summary>The same bound as the "Slow requests" trace table on the API dashboard.</summary>
    public static readonly TimeSpan SlowThreshold = TimeSpan.FromMilliseconds(250);

    /// <param name="path">The request path.</param>
    /// <param name="statusCode">The final status code.</param>
    /// <param name="elapsedMs">How long the request took.</param>
    /// <param name="exception">The exception that escaped the pipeline, if any.</param>
    /// <param name="sampled">Whether an ordinary request is sampled; called only for ordinary requests.</param>
    public static LogEventLevel Level(PathString path, int statusCode, double elapsedMs, Exception? exception, Func<bool> sampled)
    {
        ArgumentNullException.ThrowIfNull(sampled);
        if (exception is not null)
        {
            return LogEventLevel.Error;
        }

        if (path.StartsWithSegments(HealthEndpoints.PathPrefix))
        {
            return statusCode >= StatusCodes.Status500InternalServerError ? LogEventLevel.Warning : LogEventLevel.Debug;
        }

        if (statusCode >= StatusCodes.Status500InternalServerError)
        {
            return LogEventLevel.Error;
        }

        if (elapsedMs >= SlowThreshold.TotalMilliseconds
            || (statusCode >= StatusCodes.Status400BadRequest && statusCode != StatusCodes.Status429TooManyRequests))
        {
            return LogEventLevel.Information;
        }

        return sampled() ? LogEventLevel.Information : LogEventLevel.Debug;
    }

    /// <summary>
    /// Marks a request's server span as sampled, so it is exported although the sampler only recorded it. The span is
    /// still running (the request log completes inside the hosting activity); child spans of an unsampled request were
    /// never created, so the kept trace holds the server span alone.
    /// </summary>
    public static void KeepTrace(Activity? activity)
    {
        if (activity is { IsAllDataRequested: true, Recorded: false })
        {
            activity.ActivityTraceFlags |= ActivityTraceFlags.Recorded;
        }
    }
}
