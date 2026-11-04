using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using TickPulse.Application.Abstractions;
using TickPulse.Application.Symbols;
using TickPulse.Infrastructure.Backfill;

namespace TickPulse.Api.Auth;

/// <summary>Settings of the admin API (section <c>Admin</c>).</summary>
public sealed class AdminOptions
{
    public const string SectionName = "Admin";

    /// <summary>
    /// The key admins send in <c>X-Api-Key</c>. Unset, empty or blank disables the admin endpoints (403). A key with
    /// leading or trailing whitespace fails startup, since no request header could ever carry it. A key shorter than
    /// <see cref="AdminApiKey.MinLength"/> characters fails startup in the Production environment; elsewhere
    /// (development, tests) it works and is warned about at startup.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>The most symbols that may be active at once.</summary>
    [Range(1, 1000)]
    public int MaxActiveSymbols { get; set; } = 20;

    /// <summary>The startup error for a key with leading or trailing whitespace (the key itself is never included).</summary>
    public const string PaddedKeyError = "Admin:ApiKey must not start or end with whitespace; a request header could never match it.";

    /// <summary>The startup error for a short key in the Production environment (the key itself is never included).</summary>
    public static readonly string ShortKeyError =
        $"Admin:ApiKey must have at least {AdminApiKey.MinLength} characters in Production; use a long random value, or leave it empty to disable the admin endpoints.";

    /// <summary>Whether <see cref="ApiKey"/> is usable as configured: unset, blank, or without surrounding whitespace.</summary>
    public bool HasValidKey() => string.IsNullOrWhiteSpace(ApiKey) || ApiKey.Trim().Length == ApiKey.Length;

    /// <summary>
    /// Whether <see cref="ApiKey"/> is long enough for <paramref name="environment"/>: in Production a configured key
    /// needs <see cref="AdminApiKey.MinLength"/> characters; elsewhere any length passes (with a startup warning).
    /// </summary>
    public bool HasSafeLength(IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return string.IsNullOrWhiteSpace(ApiKey) || !environment.IsProduction() || ApiKey.Length >= AdminApiKey.MinLength;
    }
}

/// <summary>Wires the admin API: <see cref="AdminOptions"/>, the <c>ApiKey</c> scheme, the <see cref="Policy"/> policy and <see cref="SymbolAdminService"/>.</summary>
internal static class AdminAccess
{
    /// <summary>The authorization policy of every admin endpoint: a request authenticated by the <c>ApiKey</c> scheme.</summary>
    public const string Policy = "Admin";

    public static IServiceCollection AddTickPulseAdmin(this IServiceCollection services)
    {
        services.AddOptions<AdminOptions>()
            .BindConfiguration(AdminOptions.SectionName)
            .ValidateDataAnnotations()
            .Validate(o => o.HasValidKey(), AdminOptions.PaddedKeyError)
            .Validate<IHostEnvironment>((o, environment) => o.HasSafeLength(environment), AdminOptions.ShortKeyError)
            .ValidateOnStart();
        services.AddSingleton<AdminApiKey>();
        services.AddHostedService<AdminKeyStartupCheck>();

        // No default scheme: only the admin policy authenticates, so the public API never looks at X-Api-Key.
        services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, configureOptions: null);
        services.AddAuthorizationBuilder()
            .AddPolicy(Policy, policy => policy
                .AddAuthenticationSchemes(ApiKeyAuthenticationHandler.SchemeName)
                .RequireAuthenticatedUser());

        // Scoped: the exchange port is a typed HttpClient, which is meant to be short-lived.
        services.AddScoped(sp => new SymbolAdminService(
            sp.GetRequiredService<ISymbolRepository>(),
            sp.GetRequiredService<IExchangeMetadata>(),
            sp.GetRequiredService<ISymbolChangeSignal>(),
            sp.GetRequiredService<IBackfillQueue>(),
            new SymbolAdminSettings(
                sp.GetRequiredService<IOptions<AdminOptions>>().Value.MaxActiveSymbols,
                sp.GetRequiredService<IOptions<BackfillOptions>>().Value.Enabled)));
        return services;
    }
}

/// <summary>
/// Reports at startup whether the admin API is enabled, and warns about a short key (never logging the key). In
/// Production a short key never gets here: options validation has already failed startup.
/// </summary>
internal sealed partial class AdminKeyStartupCheck(AdminApiKey key, ILogger<AdminKeyStartupCheck> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!key.IsConfigured)
        {
            LogAdminDisabled(logger);
        }
        else if (key.Length < AdminApiKey.MinLength)
        {
            LogShortApiKey(logger, key.Length, AdminApiKey.MinLength);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Information, Message = "Admin endpoints are disabled: Admin:ApiKey is not configured")]
    private static partial void LogAdminDisabled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Admin:ApiKey has {Length} characters; use at least {Recommended} random characters (Production refuses shorter keys)")]
    private static partial void LogShortApiKey(ILogger logger, int length, int recommended);
}
