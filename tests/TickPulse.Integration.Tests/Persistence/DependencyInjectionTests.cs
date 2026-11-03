using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TickPulse.Application.Abstractions;
using TickPulse.Infrastructure;
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
    public void AddTickPulseInfrastructure_MissingConnectionString_FailsOnResolve()
    {
        using var provider = new ServiceCollection()
            .AddTickPulseInfrastructure(new ConfigurationBuilder().Build())
            .BuildServiceProvider();

        Should.Throw<InvalidOperationException>(provider.GetRequiredService<NpgsqlDataSource>)
            .Message.ShouldContain("ConnectionStrings:TickPulse");
    }
}
