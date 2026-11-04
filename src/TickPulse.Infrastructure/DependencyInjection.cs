using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using TickPulse.Application.Abstractions;
using TickPulse.Application.Ingestion;
using TickPulse.Infrastructure.Backfill;
using TickPulse.Infrastructure.Binance;
using TickPulse.Infrastructure.Ingestion;
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

        services.AddBinanceRest(configuration);

        // Shared between the API (admin use case, health, statistics) and the ingestion worker.
        services.TryAddSingleton<IngestionState>();
        services.TryAddSingleton<ISymbolChangeSignal, SymbolChangeSignal>();

        return services;
    }

    /// <summary>
    /// Registers the leader-elected ingestion worker (a hosted service) and its parts. Call it after
    /// <see cref="AddTickPulseInfrastructure"/>, whose ports it uses. Settings come from the <c>Ingestion</c> section
    /// and <c>Binance:WsBaseUrl</c>; invalid settings fail at startup.
    /// </summary>
    public static IServiceCollection AddTickPulseIngestion(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<IngestionOptions>()
            .Bind(configuration.GetSection(IngestionOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<IngestionOptions>, IngestionOptionsValidator>());

        services.TryAddSingleton<SymbolIdCache>();
        services.TryAddSingleton<IStreamConnectionFactory>(sp => new StreamConnectionFactory(
            WsBaseUrl(configuration),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance));
        services.TryAddSingleton<GapFillWatermarks>();
        services.TryAddSingleton<IGapFillWatermark>(sp => sp.GetRequiredService<GapFillWatermarks>());
        services.TryAddSingleton<TickBatchWriter>();
        services.TryAddSingleton(sp => new GapFiller(
            sp.GetRequiredService<IAggTradeSource>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetService<ILogger<GapFiller>>() ?? NullLogger<GapFiller>.Instance));
        services.TryAddSingleton(sp => new SymbolReconciler(
            sp.GetRequiredService<ISymbolRepository>(),
            sp.GetRequiredService<SymbolIdCache>(),
            sp.GetService<ILogger<SymbolReconciler>>() ?? NullLogger<SymbolReconciler>.Instance));
        // The worker is the lock's only owner, so the lock is created here and not registered.
        services.AddHostedService(sp => new IngestionWorker(
            new PostgresLeaderLock(RequiredConnectionString(configuration), sp.GetService<ILogger<PostgresLeaderLock>>()),
            sp.GetRequiredService<IStreamConnectionFactory>(),
            sp.GetRequiredService<SymbolReconciler>(),
            sp.GetRequiredService<TickBatchWriter>(),
            sp.GetRequiredService<GapFiller>(),
            sp.GetRequiredService<SymbolIdCache>(),
            sp.GetRequiredService<GapFillWatermarks>(),
            sp.GetRequiredService<ISymbolChangeSignal>(),
            sp.GetRequiredService<IngestionState>(),
            sp.GetRequiredService<IOptions<IngestionOptions>>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance));

        AddBackfill(services, configuration);
        return services;
    }

    // The backfill worker runs only while the ingestion worker holds leadership, so it is registered with it. One
    // instance is both the hosted service and the IBackfillQueue the admin use case enqueues into.
    private static void AddBackfill(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<BackfillOptions>()
            .Bind(configuration.GetSection(BackfillOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<BackfillOptions>, BackfillOptionsValidator>());

        services.TryAddSingleton(sp => new BackfillWorker(
            sp.GetRequiredService<IKlineSource>(),
            sp.GetRequiredService<ICandleWriter>(),
            sp.GetRequiredService<ISymbolRepository>(),
            sp.GetRequiredService<IMarketDataQueries>(),
            sp.GetRequiredService<IngestionState>(),
            sp.GetRequiredService<IOptions<BackfillOptions>>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetService<ILogger<BackfillWorker>>() ?? NullLogger<BackfillWorker>.Instance));
        services.TryAddSingleton<IBackfillQueue>(sp => sp.GetRequiredService<BackfillWorker>());
        services.AddHostedService(sp => sp.GetRequiredService<BackfillWorker>());
    }

    private static Uri WsBaseUrl(IConfiguration configuration)
    {
        var raw = configuration[StreamConnectionFactory.WsBaseUrlKey];
        return new Uri(string.IsNullOrWhiteSpace(raw) ? StreamConnectionFactory.DefaultWsBaseUrl : raw.Trim(), UriKind.Absolute);
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
