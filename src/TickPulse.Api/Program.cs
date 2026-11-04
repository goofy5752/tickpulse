using Microsoft.Extensions.DependencyInjection.Extensions;
using Scalar.AspNetCore;
using TickPulse.Api.Health;
using TickPulse.Api.Hosting;
using TickPulse.Api.Problems;
using TickPulse.Api.Telemetry;
using TickPulse.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
var services = builder.Services;

// First hosted service: the schema is migrated before the ingestion and backfill workers (and the server) start.
services.AddHostedService<DatabaseMigrator>();

// The workers recover from their own failures; a fault that still escapes must not stop the API.
services.Configure<HostOptions>(o => o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);

services.AddTickPulseInfrastructure(builder.Configuration);
services.AddTickPulseIngestion(builder.Configuration);
services.AddSingleton(sp => new ClientIpHasher(
    sp.GetRequiredService<IConfiguration>()[ClientIpHasher.SaltKey],
    sp.GetRequiredService<TimeProvider>()));

// MVC, because minimal APIs have no XML output formatters. JSON is the default; XML when Accept prefers it; 406
// when Accept lists nothing either can produce.
services.AddControllers(o =>
    {
        o.ReturnHttpNotAcceptable = true;
        o.RespectBrowserAcceptHeader = true;
    })
    .AddXmlDataContractSerializerFormatters();

// Every problem (controllers, status code pages, exception handler, rate limiter) goes through these two writers:
// XML when Accept prefers it, otherwise JSON. They replace the framework's JSON-only writers.
services.AddProblemDetails();
services.RemoveAll<IProblemDetailsWriter>();
services.AddSingleton<IProblemDetailsWriter, XmlProblemDetailsWriter>();
services.AddSingleton<IProblemDetailsWriter, JsonProblemDetailsWriter>();

services.AddTickPulseForwardedHeaders();
services.AddTickPulseRateLimiting();
services.AddTickPulseHealthChecks();
services.Configure<RouteOptions>(o => o.SetParameterPolicy<DashboardRouteConstraint>(DashboardRouteConstraint.Name));
services.AddOpenApi(o => o.AddDocumentTransformer((document, _, _) =>
{
    document.Info.Title = "TickPulse API";
    document.Info.Description = "Binance spot trade analytics: 24 h average price and simple moving averages, as JSON or XML.";
    return Task.CompletedTask;
}));

var app = builder.Build();

app.UseForwardedHeaders();
app.UseSecurityHeaders();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();

app.MapControllers().RequireRateLimiting(ApiRateLimiting.PublicPolicy);
app.MapTickPulseHealthChecks();
app.MapOpenApi();
app.MapScalarApiReference("/docs", o => o.WithTitle("TickPulse API"));
app.MapFallbackToFile(DashboardRouteConstraint.FallbackPattern, "index.html");

await app.RunAsync();

/// <summary>The API host; public so integration tests can start it with <c>WebApplicationFactory</c>.</summary>
public partial class Program;
