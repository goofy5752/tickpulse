using Serilog.Core;
using Serilog.Events;

namespace TickPulse.Api.Telemetry;

/// <summary>
/// Adds <c>service.name</c>, <c>service.version</c>, <c>deployment.environment</c>, <c>trace_id</c> and
/// <c>span_id</c> to the JSON console log, so a console line can be matched with its trace.
/// </summary>
/// <remarks>
/// Only the console sink uses it. The OTLP sink sends the service as resource attributes and the trace context as the
/// log record's own fields, which is what Loki indexes; repeating them as attributes would duplicate them.
/// </remarks>
internal sealed class TickPulseLogEnricher(string serviceName, string serviceVersion, string environment) : ILogEventEnricher
{
    private readonly LogEventProperty _service = new("service.name", new ScalarValue(serviceName));
    private readonly LogEventProperty _version = new("service.version", new ScalarValue(serviceVersion));
    private readonly LogEventProperty _environment = new("deployment.environment", new ScalarValue(environment));

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        logEvent.AddPropertyIfAbsent(_service);
        logEvent.AddPropertyIfAbsent(_version);
        logEvent.AddPropertyIfAbsent(_environment);
        if (logEvent.TraceId is { } traceId)
        {
            logEvent.AddPropertyIfAbsent(new LogEventProperty("trace_id", new ScalarValue(traceId.ToHexString())));
        }

        if (logEvent.SpanId is { } spanId)
        {
            logEvent.AddPropertyIfAbsent(new LogEventProperty("span_id", new ScalarValue(spanId.ToHexString())));
        }
    }
}
