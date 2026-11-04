using System.Data.Common;
using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using TickPulse.Application.Ingestion;

namespace TickPulse.Api.Health;

/// <summary>
/// <c>/health/live</c> (the process answers) and <c>/health/ready</c> (the database answers, and ingestion is either
/// streaming or standing by for another leader). Both reply with a small JSON report; not ready is a 503.
/// </summary>
internal static class HealthEndpoints
{
    public const string LivePath = "/health/live";
    public const string ReadyPath = "/health/ready";
    private const string ReadyTag = "ready";
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(5);

    public static IServiceCollection AddTickPulseHealthChecks(this IServiceCollection services)
    {
        services.AddHealthChecks()
            .AddCheck<DatabaseHealthCheck>("database", tags: [ReadyTag], timeout: CheckTimeout)
            .AddCheck<IngestionHealthCheck>("ingestion", tags: [ReadyTag]);
        return services;
    }

    public static IEndpointRouteBuilder MapTickPulseHealthChecks(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHealthChecks(LivePath, new HealthCheckOptions { Predicate = _ => false, ResponseWriter = WriteAsync });
        endpoints.MapHealthChecks(ReadyPath, new HealthCheckOptions { Predicate = r => r.Tags.Contains(ReadyTag), ResponseWriter = WriteAsync });
        return endpoints;
    }

    private static async Task WriteAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        await using var json = new Utf8JsonWriter(context.Response.Body);
        json.WriteStartObject();
        json.WriteString("status", report.Status.ToString());
        json.WriteNumber("totalDurationMs", Math.Round(report.TotalDuration.TotalMilliseconds, 1));
        json.WriteStartArray("checks");
        foreach (var (name, entry) in report.Entries)
        {
            json.WriteStartObject();
            json.WriteString("name", name);
            json.WriteString("status", entry.Status.ToString());
            json.WriteString("description", entry.Description);
            json.WriteNumber("durationMs", Math.Round(entry.Duration.TotalMilliseconds, 1));
            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteEndObject();
        await json.FlushAsync(context.RequestAborted);
    }
}

/// <summary>Healthy when a <c>SELECT 1</c> succeeds on the shared data source.</summary>
internal sealed class DatabaseHealthCheck(NpgsqlDataSource dataSource) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var command = dataSource.CreateCommand("SELECT 1");
            await command.ExecuteScalarAsync(cancellationToken);
            return HealthCheckResult.Healthy("The database answers.");
        }
        catch (DbException ex)
        {
            // The exception goes to the health check log; the public report stays generic.
            return new HealthCheckResult(context.Registration.FailureStatus, "The database does not answer.", ex);
        }
    }
}

/// <summary>Healthy while this instance streams (<see cref="WsState.Connected"/>) or stands by for another leader.</summary>
internal sealed class IngestionHealthCheck(IngestionState state) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var status = state.Status;
        var description = $"Ingestion is {status}.";
        return Task.FromResult(status is WsState.Connected or WsState.Standby
            ? HealthCheckResult.Healthy(description)
            : new HealthCheckResult(context.Registration.FailureStatus, description));
    }
}
