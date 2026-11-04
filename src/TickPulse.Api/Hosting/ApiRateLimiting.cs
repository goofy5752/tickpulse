using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using TickPulse.Api.Problems;
using TickPulse.Api.Telemetry;

namespace TickPulse.Api.Hosting;

/// <summary>Settings of the <see cref="ApiRateLimiting.PublicPolicy"/> limiter (section <c>RateLimiting</c>).</summary>
public sealed class RateLimitingOptions
{
    public const string SectionName = "RateLimiting";

    /// <summary>Requests one client IP may make per window.</summary>
    [Range(1, int.MaxValue)]
    public int PermitLimit { get; set; } = 300;

    /// <summary>Length of the fixed window, in seconds.</summary>
    [Range(1, 86_400)]
    public int WindowSeconds { get; set; } = 60;
}

/// <summary>
/// A fixed window per client on the API controllers: the IPv4 address or the IPv6 /64 prefix, after
/// <see cref="ClientAddressing"/>. A rejected request gets a 429 <c>rate-limited</c> problem with <c>Retry-After</c>
/// set to the time left in the client's window (<see cref="ClientWindowRateLimiter"/>), negotiated like every other
/// problem.
/// </summary>
/// <remarks>
/// The first rejection of a client in a window logs a Warning; the rest of that window's rejections log at Debug, so a
/// flood of requests cannot flood the logs. ASP.NET Core's <c>aspnetcore.rate_limiting.requests</c> metric still counts
/// every rejection.
/// </remarks>
internal static partial class ApiRateLimiting
{
    public const string PublicPolicy = "public";

    public static IServiceCollection AddTickPulseRateLimiting(this IServiceCollection services)
    {
        services.AddOptions<RateLimitingOptions>()
            .BindConfiguration(RateLimitingOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(PublicPolicy, context =>
            {
                var settings = context.RequestServices.GetRequiredService<IOptions<RateLimitingOptions>>().Value;
                var clock = context.RequestServices.GetRequiredService<TimeProvider>();
                var key = ClientAddressing.PartitionKey(context.Connection.RemoteIpAddress);
                return RateLimitPartition.Get(
                    key,
                    _ => new ClientWindowRateLimiter(settings.PermitLimit, TimeSpan.FromSeconds(settings.WindowSeconds), clock));
            });
            options.OnRejected = OnRejectedAsync;
        });
        return services;
    }

    private static async ValueTask OnRejectedAsync(OnRejectedContext rejected, CancellationToken cancellationToken)
    {
        var context = rejected.HttpContext;
        var services = context.RequestServices;
        int? retryAfterSeconds = rejected.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)
            ? Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds))
            : null;
        if (retryAfterSeconds is { } seconds)
        {
            context.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        }

        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(ApiRateLimiting).FullName!);
        var first = !rejected.Lease.TryGetMetadata(ClientWindowRateLimiter.RejectionsInWindow, out var rejections) || rejections <= 1;
        if (first || logger.IsEnabled(LogLevel.Debug))
        {
            var client = services.GetRequiredService<ClientIpHasher>().Hash(ClientAddressing.ClientAddress(context));
            if (first)
            {
                LogRejected(logger, client, context.Request.Path, retryAfterSeconds);
            }
            else
            {
                LogRejectedAgain(logger, client, context.Request.Path, rejections);
            }
        }

        var detail = retryAfterSeconds is { } wait
            ? string.Create(CultureInfo.InvariantCulture, $"Too many requests from this client; retry after {wait} s.")
            : "Too many requests from this client.";
        await services.GetRequiredService<IProblemDetailsService>().WriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = ProblemFactory.Create(context, ProblemSlugs.RateLimited, StatusCodes.Status429TooManyRequests, detail),
        });
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Rate limit exceeded by client {ClientIpHash} on {Path}; its window ends in {RetryAfterSeconds} s, further rejections until then log at Debug")]
    private static partial void LogRejected(ILogger logger, string clientIpHash, PathString path, int? retryAfterSeconds);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Rate limit exceeded by client {ClientIpHash} on {Path} ({Rejections} rejections in this window)")]
    private static partial void LogRejectedAgain(ILogger logger, string clientIpHash, PathString path, int rejections);
}
