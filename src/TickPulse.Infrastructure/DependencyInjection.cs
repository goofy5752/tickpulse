using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using TickPulse.Application.Abstractions;
using TickPulse.Infrastructure.Persistence;

namespace TickPulse.Infrastructure;

/// <summary>Registers the infrastructure services.</summary>
public static class DependencyInjection
{
    public const string ConnectionStringName = "TickPulse";

    /// <summary>
    /// Registers one <see cref="NpgsqlDataSource"/> singleton built from <c>ConnectionStrings:TickPulse</c>, the
    /// EF Core context on top of it, and the Npgsql persistence ports.
    /// </summary>
    public static IServiceCollection AddTickPulseInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddSingleton(TimeProvider.System);

        services.AddSingleton(_ =>
        {
            var connectionString = configuration.GetConnectionString(ConnectionStringName);
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException($"ConnectionStrings:{ConnectionStringName} is not configured.");
            }

            return NpgsqlDataSource.Create(connectionString);
        });

        services.AddDbContext<TickPulseDbContext>((sp, options) => options.UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>()));

        services.AddSingleton<ITickWriter, TickWriter>();
        services.AddSingleton<ICandleWriter, CandleWriter>();
        services.AddSingleton<ISymbolRepository, SymbolRepository>();

        return services;
    }
}
