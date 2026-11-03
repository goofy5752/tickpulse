using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;
using TickPulse.Application.Abstractions;
using TickPulse.Infrastructure.Persistence;

namespace TickPulse.Infrastructure;

/// <summary>Registers the infrastructure services.</summary>
public static class DependencyInjection
{
    public const string ConnectionStringName = "TickPulse";

    /// <summary>Service key of the <see cref="NpgsqlDataSource"/> that only <see cref="TickWriter"/> uses.</summary>
    public const string TickWriterDataSourceKey = "tick-writer";

    /// <summary>Upper bound on the tick writer's connections; batches are written one at a time.</summary>
    public const int TickWriterMaxPoolSize = 4;

    /// <summary>
    /// Registers the shared <see cref="NpgsqlDataSource"/> singleton built from <c>ConnectionStrings:TickPulse</c>,
    /// the EF Core context on top of it, a keyed data source for the tick writer, and the Npgsql persistence ports
    /// and read-side queries.
    /// </summary>
    public static IServiceCollection AddTickPulseInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddSingleton(TimeProvider.System);

        services.AddSingleton(sp => BuildDataSource(RequiredConnectionString(configuration), sp.GetService<ILoggerFactory>()));
        services.AddKeyedSingleton(
            TickWriterDataSourceKey,
            (sp, _) => BuildTickWriterDataSource(RequiredConnectionString(configuration), sp.GetService<ILoggerFactory>()));

        services.AddDbContext<TickPulseDbContext>((sp, options) => options.UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>()));

        services.AddSingleton<ITickWriter>(sp => new TickWriter(
            sp.GetRequiredKeyedService<NpgsqlDataSource>(TickWriterDataSourceKey),
            sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<ICandleWriter, CandleWriter>();
        services.AddSingleton<ISymbolRepository, SymbolRepository>();
        services.AddSingleton<IMarketDataQueries, MarketDataQueries>();

        return services;
    }

    /// <summary>The shared data source used by every component except the tick writer.</summary>
    public static NpgsqlDataSource BuildDataSource(string connectionString, ILoggerFactory? loggerFactory)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        if (loggerFactory is not null)
        {
            builder.UseLoggerFactory(loggerFactory);
        }

        return builder.Build();
    }

    /// <summary>
    /// The tick writer's own data source. <c>NoResetOnClose</c> skips the <c>DISCARD ALL</c> that pooled
    /// connections otherwise run on reuse, so the session temp table <c>tick_staging</c> survives between
    /// batches instead of being created again every time.
    /// </summary>
    /// <remarks>
    /// The cached temp table keeps the shape <c>price_ticks</c> had when it was created. That is acceptable
    /// because migrations run before the writer starts. It is always empty between batches: a committed
    /// batch is cleared by <c>ON COMMIT DELETE ROWS</c>, a failed one by its rollback.
    /// </remarks>
    public static NpgsqlDataSource BuildTickWriterDataSource(string connectionString, ILoggerFactory? loggerFactory)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        builder.ConnectionStringBuilder.NoResetOnClose = true;
        builder.ConnectionStringBuilder.MaxPoolSize = TickWriterMaxPoolSize;
        if (loggerFactory is not null)
        {
            builder.UseLoggerFactory(loggerFactory);
        }

        return builder.Build();
    }

    private static string RequiredConnectionString(IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName);
        return string.IsNullOrWhiteSpace(connectionString)
            ? throw new InvalidOperationException($"ConnectionStrings:{ConnectionStringName} is not configured.")
            : connectionString;
    }
}
