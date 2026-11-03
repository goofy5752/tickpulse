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

    /// <summary>
    /// Registers <see cref="BinanceRestClient"/> as a typed <see cref="HttpClient"/> behind the standard resilience
    /// handler, a singleton <see cref="BinanceWeightGuard"/>, and the client as <see cref="IExchangeMetadata"/>,
    /// <see cref="IKlineSource"/> and <see cref="IAggTradeSource"/>.
    /// </summary>
    /// <remarks>
    /// The standard pipeline's retry and circuit breaker ignore <c>429</c> and <c>418</c>. Binance meters weight per
    /// IP per minute, so a fast pipeline retry of a 429 spends weight in the very window that is exhausted and is
    /// how an IP gets banned; a circuit breaker opened by 429s would also hide <c>Retry-After</c>. Those two
    /// statuses reach <see cref="BinanceRestClient"/>, which waits on the shared <see cref="BinanceWeightGuard"/>
    /// (and its <see cref="TimeProvider"/>). 5xx, 408 and network failures are still retried by the pipeline.
    /// <para>The base URL is read when the client is created, so configuration added after this call still applies.</para>
    /// </remarks>
    public static IServiceCollection AddBinanceRest(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<BinanceWeightGuard>();

        services.AddHttpClient<BinanceRestClient>(client => client.BaseAddress = RestBaseUrl(configuration))
            .AddStandardResilienceHandler(options =>
            {
                options.Retry.ShouldHandle = args => ValueTask.FromResult(IsTransientFailure(args.Outcome));
                options.CircuitBreaker.ShouldHandle = args => ValueTask.FromResult(IsTransientFailure(args.Outcome));
            });

        services.TryAddTransient<IExchangeMetadata>(sp => sp.GetRequiredService<BinanceRestClient>());
        services.TryAddTransient<IKlineSource>(sp => sp.GetRequiredService<BinanceRestClient>());
        services.TryAddTransient<IAggTradeSource>(sp => sp.GetRequiredService<BinanceRestClient>());
        return services;
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
