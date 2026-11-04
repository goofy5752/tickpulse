using Microsoft.Extensions.DependencyInjection.Extensions;
using Scalar.AspNetCore;
using TickPulse.Api.Auth;
using TickPulse.Api.Health;
using TickPulse.Api.Hosting;
using TickPulse.Api.Hubs;
using TickPulse.Api.Problems;
using TickPulse.Api.Telemetry;
using TickPulse.Application.Analytics;
using TickPulse.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
builder.AddTickPulseTelemetry();
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
    .ConfigureApiBehaviorOptions(o =>
    {
        // Parameters that cannot be bound (n=abc, limit=abc) get the same validation problem, negotiated, as every other error.
        o.InvalidModelStateResponseFactory = context =>
            new ProblemResult(ProblemFactory.CreateValidation(context.HttpContext, context.ModelState));

        // MVC's own client errors (415 for an unsupported body, ...) stay bare status codes, so the status code pages
        // turn them into TickPulse problems through the writers below, like routing's 404 and 405.
        o.SuppressMapClientErrors = true;
    })
    .AddXmlDataContractSerializerFormatters();

services.AddSingleton<AnalyticsService>();
services.AddTickPulseAdmin();

// Every problem (controllers, status code pages, exception handler, rate limiter) goes through these two writers:
// XML when Accept prefers it, otherwise JSON. They replace the framework's JSON-only writers.
services.AddProblemDetails();
services.RemoveAll<IProblemDetailsWriter>();
services.AddSingleton<IProblemDetailsWriter, XmlProblemDetailsWriter>();
services.AddSingleton<IProblemDetailsWriter, JsonProblemDetailsWriter>();

services.AddTickPulseForwardedHeaders();
services.AddTickPulseDashboardAssets();
services.AddTickPulseRateLimiting();
services.AddTickPulseHealthChecks();
services.AddTickPulseMarketHub();
services.Configure<RouteOptions>(o => o.SetParameterPolicy<DashboardRouteConstraint>(DashboardRouteConstraint.Name));
services.AddOpenApi(o => o.AddDocumentTransformer((document, _, _) =>
{
    document.Info.Title = "TickPulse API";
    document.Info.Description = "Binance spot trade analytics: 24 h average price and simple moving averages, as JSON or XML.";
    return Task.CompletedTask;
}));

var app = builder.Build();

app.UseForwardedHeaders();
app.UseTickPulseTelemetry(); // request log and API metrics: after the forwarded headers, before everything else
app.UseHeadWithoutBody();
app.UseSecurityHeaders();
app.UseVaryByAcceptOnApi();
app.UseAcceptHeaderNormalization();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseDashboardAssetCaching();
app.UseRouting();

// After routing, so a file in the static assets manifest is served by its MapStaticAssets endpoint (precompressed,
// per-encoding ETag); UseStaticFiles only serves files no endpoint matched, such as a web root filled after the build.
app.UseStaticFiles();
app.UseRateLimiter();

// After the rate limiter, so failed key guesses are throttled like any other request.
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers().RequireRateLimiting(ApiRateLimiting.PublicPolicy).WithHeadForGet();
app.MapTickPulseHealthChecks();
app.MapTickPulseMarketHub(); // before the dashboard fallback
app.MapOpenApi();
app.MapScalarApiReference("/docs", o => o.WithTitle("TickPulse API"));
app.MapDashboardAssets();
app.MapFallbackToFile(DashboardRouteConstraint.FallbackPattern, "index.html");

await app.RunAsync();

/// <summary>The API host; public so integration tests can start it with <c>WebApplicationFactory</c>.</summary>
public partial class Program;
