using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;
using TickPulse.Application.Abstractions;
using TickPulse.Application.Ingestion;
using TickPulse.Infrastructure;
using TickPulse.Infrastructure.Binance;
using TickPulse.Infrastructure.Ingestion;
using TickPulse.Infrastructure.Persistence;
using TickPulse.Integration.Tests.Fixtures;

namespace TickPulse.Integration.Tests.Persistence;

public sealed class DependencyInjectionTests(TimescaleFixture db)
{
    [Fact]
    public async Task AddTickPulseInfrastructure_RegistersPortsAndDataSources()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:TickPulse"] = db.ConnectionString })
            .Build();
        await using var provider = new ServiceCollection()
            .AddLogging()
            .AddTickPulseInfrastructure(configuration)
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

        provider.GetRequiredService<ITickWriter>().ShouldBeOfType<TickWriter>();
        provider.GetRequiredService<ICandleWriter>().ShouldBeOfType<CandleWriter>();
        provider.GetRequiredService<ISymbolRepository>().ShouldBeOfType<SymbolRepository>();
        provider.GetRequiredService<IMarketDataQueries>().ShouldBeOfType<MarketDataQueries>();
        provider.GetRequiredService<TimeProvider>().ShouldBe(TimeProvider.System);

        var shared = provider.GetRequiredService<NpgsqlDataSource>();
        var writer = provider.GetRequiredKeyedService<NpgsqlDataSource>(DependencyInjection.TickWriterDataSourceKey);
        shared.ShouldBeSameAs(provider.GetRequiredService<NpgsqlDataSource>());
        writer.ShouldBeSameAs(provider.GetRequiredKeyedService<NpgsqlDataSource>(DependencyInjection.TickWriterDataSourceKey));
        writer.ShouldNotBeSameAs(shared);
        var writerSettings = new NpgsqlConnectionStringBuilder(writer.ConnectionString);
        writerSettings.NoResetOnClose.ShouldBeTrue();
        writerSettings.MaxPoolSize.ShouldBe(DependencyInjection.TickWriterMaxPoolSize);
        new NpgsqlConnectionStringBuilder(shared.ConnectionString).NoResetOnClose.ShouldBeFalse();

        var active = await provider.GetRequiredService<ISymbolRepository>().CountActiveAsync(TestContext.Current.CancellationToken);
        active.ShouldBeGreaterThanOrEqualTo(0);

        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TickPulseDbContext>();
        (await context.Symbols.CountAsync(TestContext.Current.CancellationToken)).ShouldBeGreaterThanOrEqualTo(active);
    }

    [Fact]
    public async Task AddTickPulseIngestion_RegistersWorkerAndSharedState()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:TickPulse"] = db.ConnectionString })
            .Build();
        await using var provider = new ServiceCollection()
            .AddLogging()
            .AddTickPulseInfrastructure(configuration)
            .AddTickPulseIngestion(configuration)
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

        provider.GetServices<IHostedService>().ShouldHaveSingleItem().GetType().Name.ShouldBe("IngestionWorker");
        provider.GetRequiredService<ISymbolChangeSignal>().ShouldBeOfType<SymbolChangeSignal>();
        provider.GetRequiredService<ISymbolChangeSignal>().ShouldBeSameAs(provider.GetRequiredService<ISymbolChangeSignal>());
        provider.GetRequiredService<IngestionState>().ShouldBeSameAs(provider.GetRequiredService<IngestionState>());
        provider.GetRequiredService<IAggTradeSource>().ShouldBeOfType<BinanceRestClient>();
        provider.GetRequiredService<PostgresLeaderLock>().IsHeld.ShouldBeFalse();

        var options = provider.GetRequiredService<IOptions<IngestionOptions>>().Value;
        options.RotationInterval.ShouldBe(TimeSpan.FromHours(23));
        options.BatchMaxSize.ShouldBe(5000);
        options.BatchMaxDelay.ShouldBe(TimeSpan.FromMilliseconds(500));
        options.ChannelCapacity.ShouldBe(100_000);
        options.ReconcileInterval.ShouldBe(TimeSpan.FromSeconds(30));
        options.LeaderRetryInterval.ShouldBe(TimeSpan.FromSeconds(10));
    }

    [Theory]
    [InlineData("Ingestion:RotationInterval", "1.00:00:00")]
    [InlineData("Ingestion:BatchMaxSize", "0")]
    [InlineData("Ingestion:BatchMaxDelay", "00:00:00")]
    [InlineData("Ingestion:ChannelCapacity", "0")]
    [InlineData("Ingestion:ReconcileInterval", "-00:00:01")]
    [InlineData("Ingestion:LeaderRetryInterval", "00:00:00")]
    [InlineData("Ingestion:LeaderCheckInterval", "00:00:00")]
    public void AddTickPulseIngestion_InvalidSetting_FailsValidation(string key, string value)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:TickPulse"] = db.ConnectionString,
                [key] = value,
            })
            .Build();
        using var provider = new ServiceCollection()
            .AddTickPulseIngestion(configuration)
            .BuildServiceProvider();

        Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IOptions<IngestionOptions>>().Value)
            .Message.ShouldContain(key.Split(':')[1]);
    }

    [Fact]
    public void AddTickPulseInfrastructure_MissingConnectionString_FailsOnResolve()
    {
        using var provider = new ServiceCollection()
            .AddTickPulseInfrastructure(new ConfigurationBuilder().Build())
            .BuildServiceProvider();

        Should.Throw<InvalidOperationException>(provider.GetRequiredService<NpgsqlDataSource>)
            .Message.ShouldContain("ConnectionStrings:TickPulse");
    }
}
