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
/// <see cref="ClientAddressing"/>. A rejected request gets a 429 <c>rate-limited</c> problem with <c>Retry-After</c>,
/// negotiated like every other problem.
/// </summary>
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
                var key = ClientAddressing.PartitionKey(context.Connection.RemoteIpAddress);
                return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = settings.PermitLimit,
                    Window = TimeSpan.FromSeconds(settings.WindowSeconds),
                    QueueLimit = 0,
                });
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
        LogRejected(logger, services.GetRequiredService<ClientIpHasher>().Hash(ClientAddressing.ClientAddress(context)), context.Request.Path);

        var detail = retryAfterSeconds is { } wait
            ? string.Create(CultureInfo.InvariantCulture, $"Too many requests from this client; retry after {wait} s.")
            : "Too many requests from this client.";
        await services.GetRequiredService<IProblemDetailsService>().WriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = ProblemFactory.Create(context, ProblemSlugs.RateLimited, StatusCodes.Status429TooManyRequests, detail),
        });
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rate limit exceeded by client {ClientIpHash} on {Path}")]
    private static partial void LogRejected(ILogger logger, string clientIpHash, PathString path);
}
