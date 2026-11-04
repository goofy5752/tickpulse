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

    /// <summary>The key admins send in <c>X-Api-Key</c>. Empty or unset disables the admin endpoints (403).</summary>
    public string? ApiKey { get; set; }

    /// <summary>The most symbols that may be active at once.</summary>
    [Range(1, 1000)]
    public int MaxActiveSymbols { get; set; } = 20;
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
            .ValidateOnStart();

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
