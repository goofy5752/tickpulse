using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using TickPulse.Application.Abstractions;

namespace TickPulse.Infrastructure.Binance;

/// <summary>DI registration for the Binance REST client.</summary>
public static class BinanceServiceCollectionExtensions
{
    /// <summary>Configuration key of the REST base URL.</summary>
    public const string RestBaseUrlKey = "Binance:RestBaseUrl";

    /// <summary>Used when <see cref="RestBaseUrlKey"/> is not set.</summary>
    public const string DefaultRestBaseUrl = "https://api.binance.com";

    /// <summary>Name of the typed client and of its <c>IHttpClientFactory</c> configuration.</summary>
    public const string RestClientName = nameof(BinanceRestClient);

    /// <summary>Options name of the client's <see cref="HttpStandardResilienceOptions"/>.</summary>
    public const string RestResilienceOptionsName = RestClientName + "-standard";

    /// <summary>How long a pooled connection lives before it is replaced, so DNS changes are picked up.</summary>
    public static readonly TimeSpan PooledConnectionLifetime = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Registers <see cref="BinanceRestClient"/> as a typed <see cref="HttpClient"/> behind the standard resilience
    /// handler, a singleton <see cref="BinanceWeightGuard"/>, the client wrapped in <see cref="BinanceExchangeMetadata"/> as
    /// <see cref="IExchangeMetadata"/>, and the client itself as
    /// <see cref="IKlineSource"/> and <see cref="IAggTradeSource"/>.
    /// </summary>
    /// <remarks>
    /// <para>The standard pipeline's retry and circuit breaker ignore <c>429</c> and <c>418</c>. Binance meters
    /// weight per IP per minute, so a fast pipeline retry of a 429 spends weight in the very window that is exhausted
    /// and is how an IP gets banned; a circuit breaker opened by 429s would also hide <c>Retry-After</c>. Those two
    /// statuses reach <see cref="BinanceRestClient"/>, which waits on the shared <see cref="BinanceWeightGuard"/>
    /// (and its <see cref="TimeProvider"/>). 5xx, 408, timeouts and network failures are still retried by the
    /// pipeline; those inner retries do not wait on the guard (see <see cref="BinanceAttemptHandler"/>).</para>
    /// <para>The ingestion and backfill workers are singletons and keep their client for the life of the process,
    /// so the handler is never rotated (<c>SetHandlerLifetime(Infinite)</c>) and DNS is refreshed by the primary
    /// <see cref="SocketsHttpHandler"/>'s <see cref="PooledConnectionLifetime"/> instead.</para>
    /// <para>Polly takes its clock from a <see cref="TimeProvider"/> registered in DI. Tests that register a
    /// <c>FakeTimeProvider</c> should set <c>Retry.Delay</c> to zero through
    /// <see cref="ConfigureBinanceRestResilience"/>, or a 5xx retry waits on the frozen clock.</para>
    /// <para>The base URL is read when the client is created, so configuration added after this call still applies.</para>
    /// </remarks>
    public static IServiceCollection AddBinanceRest(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<BinanceWeightGuard>();

        var client = services
            .AddHttpClient<BinanceRestClient>(RestClientName, http => http.BaseAddress = RestBaseUrl(configuration))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { PooledConnectionLifetime = PooledConnectionLifetime })
            .SetHandlerLifetime(Timeout.InfiniteTimeSpan);

        // Handlers run outermost first: the resilience pipeline wraps the per-attempt handler.
        client.AddStandardResilienceHandler(options =>
        {
            options.Retry.ShouldHandle = args => ValueTask.FromResult(IsTransientFailure(args.Outcome));
            options.CircuitBreaker.ShouldHandle = args => ValueTask.FromResult(IsTransientFailure(args.Outcome));
        });
        client.AddHttpMessageHandler(sp => new BinanceAttemptHandler(sp.GetRequiredService<BinanceWeightGuard>()));

        services.TryAddTransient<IExchangeMetadata>(sp => new BinanceExchangeMetadata(
            sp.GetRequiredService<BinanceRestClient>(),
            sp.GetRequiredService<TimeProvider>()));
        services.TryAddTransient<IKlineSource>(sp => sp.GetRequiredService<BinanceRestClient>());
        services.TryAddTransient<IAggTradeSource>(sp => sp.GetRequiredService<BinanceRestClient>());
        return services;
    }

    /// <summary>
    /// Adjusts the client's standard resilience options after <see cref="AddBinanceRest"/>, e.g. in tests:
    /// <c>services.ConfigureBinanceRestResilience(o =&gt; o.Retry.Delay = TimeSpan.Zero)</c>.
    /// </summary>
    public static IServiceCollection ConfigureBinanceRestResilience(
        this IServiceCollection services, Action<HttpStandardResilienceOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        return services.Configure(RestResilienceOptionsName, configure);
    }

    /// <summary>The standard transient predicate (5xx, 408, 429, network errors, timeouts) minus 429 and 418.</summary>
    internal static bool IsTransientFailure(Outcome<HttpResponseMessage> outcome) =>
        outcome.Result?.StatusCode is not (HttpStatusCode.TooManyRequests or (HttpStatusCode)418)
        && HttpClientResiliencePredicates.IsTransient(outcome);

    // Relative request URIs ("api/v3/...") keep any base path, which needs a trailing slash.
    private static Uri RestBaseUrl(IConfiguration configuration)
    {
        var raw = configuration[RestBaseUrlKey];
        var url = string.IsNullOrWhiteSpace(raw) ? DefaultRestBaseUrl : raw.Trim();
        return new Uri(url.EndsWith('/') ? url : url + "/", UriKind.Absolute);
    }
}
