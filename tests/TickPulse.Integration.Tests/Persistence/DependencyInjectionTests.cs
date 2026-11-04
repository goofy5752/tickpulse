using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using TickPulse.Application.Abstractions;
using TickPulse.Application.Ingestion;
using TickPulse.Infrastructure;
using TickPulse.Infrastructure.Backfill;
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
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:TickPulse"] = db.RawConnectionString })
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
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:TickPulse"] = db.RawConnectionString })
            .Build();
        await using var provider = new ServiceCollection()
            .AddLogging()
            .AddTickPulseInfrastructure(configuration)
            .AddTickPulseIngestion(configuration)
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

        var hosted = provider.GetServices<IHostedService>().ToArray();
        hosted.Select(h => h.GetType().Name).ShouldBe(["IngestionWorker", "BackfillWorker"]);
        provider.GetRequiredService<IBackfillQueue>().ShouldBeSameAs(hosted[1]);
        provider.GetRequiredService<IBackfillQueue>().ShouldBeSameAs(provider.GetRequiredService<IBackfillQueue>());
        provider.GetRequiredService<IKlineSource>().ShouldBeOfType<BinanceRestClient>();
        var backfill = provider.GetRequiredService<IOptions<BackfillOptions>>().Value;
        backfill.Enabled.ShouldBeFalse();
        backfill.Depth.ShouldBe(TimeSpan.FromDays(182));
        provider.GetRequiredService<ISymbolChangeSignal>().ShouldBeOfType<SymbolChangeSignal>();
        provider.GetRequiredService<ISymbolChangeSignal>().ShouldBeSameAs(provider.GetRequiredService<ISymbolChangeSignal>());
        provider.GetRequiredService<IngestionState>().ShouldBeSameAs(provider.GetRequiredService<IngestionState>());
        provider.GetRequiredService<IAggTradeSource>().ShouldBeOfType<BinanceRestClient>();
        provider.GetService<PostgresLeaderLock>().ShouldBeNull(); // owned by the worker alone
        provider.GetRequiredService<IGapFillWatermark>().ShouldBeSameAs(provider.GetRequiredService<GapFillWatermarks>());

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
                ["ConnectionStrings:TickPulse"] = db.RawConnectionString,
                [key] = value,
            })
            .Build();
        using var provider = new ServiceCollection()
            .AddTickPulseIngestion(configuration)
            .BuildServiceProvider();

        Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IOptions<IngestionOptions>>().Value)
            .Message.ShouldContain(key.Split(':')[1]);
    }

    [Theory]
    [InlineData("00:00:00")]
    [InlineData("00:00:59")]
    [InlineData("-1.00:00:00")]
    [InlineData("1096.00:00:00")]
    public void AddTickPulseIngestion_InvalidBackfillDepth_FailsValidation(string value)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:TickPulse"] = db.RawConnectionString,
                ["Backfill:Depth"] = value,
            })
            .Build();
        using var provider = new ServiceCollection()
            .AddTickPulseIngestion(configuration)
            .BuildServiceProvider();

        Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IOptions<BackfillOptions>>().Value)
            .Message.ShouldContain("Depth");
    }

    // Require is the discriminating case: the test database has no GSS encryption, so a connection that kept the
    // mode could not open at all. The queries below open real connections on every data source and the context.
    [Theory]
    [InlineData("")]
    [InlineData(";GSS Encryption Mode=Prefer")]
    [InlineData(";GSS Encryption Mode=Require")]
    public async Task AddTickPulseInfrastructure_EveryDataSourceAndTheDbContext_HaveGssEncryptionDisabled(string setting)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:TickPulse"] = db.RawConnectionString + setting })
            .Build();
        await using var provider = new ServiceCollection()
            .AddLogging()
            .AddTickPulseInfrastructure(configuration)
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        var ct = TestContext.Current.CancellationToken;

        var shared = provider.GetRequiredService<NpgsqlDataSource>();
        var writer = provider.GetRequiredKeyedService<NpgsqlDataSource>(DependencyInjection.TickWriterDataSourceKey);
        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TickPulseDbContext>();

        GssMode(shared.ConnectionString).ShouldBe(GssEncryptionMode.Disable);
        GssMode(writer.ConnectionString).ShouldBe(GssEncryptionMode.Disable);
        GssMode(context.Database.GetDbConnection().ConnectionString).ShouldBe(GssEncryptionMode.Disable);

        await using (var command = shared.CreateCommand("SELECT 1"))
        {
            (await command.ExecuteScalarAsync(ct)).ShouldBe(1);
        }

        await using (var command = writer.CreateCommand("SELECT 1"))
        {
            (await command.ExecuteScalarAsync(ct)).ShouldBe(1);
        }

        // The migrator goes through this same context.
        (await context.Database.GetAppliedMigrationsAsync(ct)).ShouldNotBeEmpty();
    }

    [Theory]
    [InlineData(";GSS Encryption Mode=Prefer", true)]
    [InlineData(";GSS Encryption Mode=Disable", false)]
    [InlineData("", false)]
    public void AddTickPulseInfrastructure_AnIgnoredGssEncryptionMode_IsLoggedAsAWarning(string setting, bool warned)
    {
        using var logs = new FakeLoggerProvider();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:TickPulse"] = db.RawConnectionString + setting })
            .Build();
        using var provider = new ServiceCollection()
            .AddLogging(b => b.AddProvider(logs))
            .AddTickPulseInfrastructure(configuration)
            .BuildServiceProvider();

        _ = provider.GetRequiredService<NpgsqlDataSource>();

        var warnings = logs.Collector.GetSnapshot().Where(r => r.Level == LogLevel.Warning && r.Message.Contains("GSS Encryption Mode", StringComparison.Ordinal)).ToList();
        warnings.Count.ShouldBe(warned ? 1 : 0);
        if (warned)
        {
            warnings[0].Message.ShouldBe(
                "GSS Encryption Mode=Prefer in the connection string is ignored; TickPulse disables GSS encryption unless Database:AllowGssEncryption=true");
        }
    }

    [Fact]
    public void AddTickPulseInfrastructure_WithTheOptIn_KeepsAnExplicitGssEncryptionMode()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:TickPulse"] = db.RawConnectionString + ";GSS Encryption Mode=Require",
                [NpgsqlConnectionPolicy.AllowGssEncryptionKey] = "true",
            })
            .Build();
        using var provider = new ServiceCollection()
            .AddTickPulseInfrastructure(configuration)
            .BuildServiceProvider();

        GssMode(provider.GetRequiredService<NpgsqlDataSource>().ConnectionString).ShouldBe(GssEncryptionMode.Require);
        GssMode(provider.GetRequiredKeyedService<NpgsqlDataSource>(DependencyInjection.TickWriterDataSourceKey).ConnectionString)
            .ShouldBe(GssEncryptionMode.Require);
    }

    [Theory]
    [InlineData(";GSS Encryption Mode=Prefer")]
    [InlineData(";GSS Encryption Mode=Require")]
    public async Task PostgresLeaderLock_HasGssEncryptionDisabled_AndAcquires(string setting)
    {
        await using var leaderLock = new PostgresLeaderLock(db.RawConnectionString + setting);

        GssMode(leaderLock.ConnectionString).ShouldBe(GssEncryptionMode.Disable);
        (await leaderLock.TryAcquireAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();
        await leaderLock.ReleaseAsync();
    }

    [Fact]
    public async Task PostgresLeaderLock_WithTheOptIn_KeepsAnExplicitGssEncryptionMode()
    {
        await using var leaderLock = new PostgresLeaderLock(db.RawConnectionString + ";GSS Encryption Mode=Require", settings: new NpgsqlPolicySettings(AllowGssEncryption: true));

        GssMode(leaderLock.ConnectionString).ShouldBe(GssEncryptionMode.Require);
    }

    [Fact]
    public void BuildDataSources_ForceGssEncryptionOff_ForDirectCallersToo()
    {
        var connectionString = db.RawConnectionString + ";GSS Encryption Mode=Require";

        using var shared = DependencyInjection.BuildDataSource(connectionString, null);
        using var writer = DependencyInjection.BuildTickWriterDataSource(connectionString, null);

        GssMode(shared.ConnectionString).ShouldBe(GssEncryptionMode.Disable);
        GssMode(writer.ConnectionString).ShouldBe(GssEncryptionMode.Disable);
    }

    [Theory]
    [InlineData("", null, 20)]
    [InlineData("", "7", 7)]
    [InlineData(";Maximum Pool Size=50", "7", 50)]
    public void AddTickPulseInfrastructure_SharedPool_IsBounded_WriterKeepsItsOwnSize(string setting, string? configured, int expected)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:TickPulse"] = db.RawConnectionString + setting,
                [NpgsqlConnectionPolicy.MaxPoolSizeKey] = configured,
            })
            .Build();
        using var provider = new ServiceCollection()
            .AddTickPulseInfrastructure(configuration)
            .BuildServiceProvider();

        new NpgsqlConnectionStringBuilder(provider.GetRequiredService<NpgsqlDataSource>().ConnectionString).MaxPoolSize.ShouldBe(expected);
        new NpgsqlConnectionStringBuilder(provider.GetRequiredKeyedService<NpgsqlDataSource>(DependencyInjection.TickWriterDataSourceKey).ConnectionString)
            .MaxPoolSize.ShouldBe(DependencyInjection.TickWriterMaxPoolSize);
    }

    [Fact]
    public void AddTickPulseInfrastructure_InvalidMaxPoolSize_FailsOnResolve()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:TickPulse"] = db.RawConnectionString,
                [NpgsqlConnectionPolicy.MaxPoolSizeKey] = "0",
            })
            .Build();
        using var provider = new ServiceCollection()
            .AddTickPulseInfrastructure(configuration)
            .BuildServiceProvider();

        Should.Throw<InvalidOperationException>(provider.GetRequiredService<NpgsqlDataSource>).Message.ShouldContain("Database:MaxPoolSize");
    }

    // The test server runs PostgreSQL's defaults: max_connections 100, 3 reserved for superusers.
    [Theory]
    [InlineData(25, false)]
    [InlineData(95, true)]
    public async Task CheckBudgetAsync_WarnsWhenTheApiCouldTakeTheHeadroom(int budget, bool warned)
    {
        using var logs = new FakeLoggerProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(logs));
        await using var dataSource = DependencyInjection.BuildDataSource(db.RawConnectionString, null);

        var available = await NpgsqlConnectionPolicy.CheckBudgetAsync(
            dataSource, budget, factory.CreateLogger("test"), TimeProvider.System, TestContext.Current.CancellationToken);

        available.ShouldBe(97);
        var warnings = logs.Collector.GetSnapshot().Where(r => r.Level == LogLevel.Warning).ToList();
        warnings.Count.ShouldBe(warned ? 1 : 0);
        if (warned)
        {
            warnings[0].Message.ShouldBe(
                "The API may open 95 database connections, but PostgreSQL allows 97 for ordinary roles and 10 should stay free; "
                + "lower Database:MaxPoolSize (or Maximum Pool Size) or raise max_connections");
        }
    }

    [Fact]
    public async Task CheckBudgetAsync_UnreachableServer_LogsThatItCouldNotRun_AtInformation()
    {
        using var logs = new FakeLoggerProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(logs));
        // Port 1 refuses at once; a non-Npgsql failure (the refused socket) must not escape either.
        await using var dataSource = DependencyInjection.BuildDataSource("Host=127.0.0.1;Port=1;Username=x;Password=x;Timeout=2", null);

        var available = await NpgsqlConnectionPolicy.CheckBudgetAsync(
            dataSource, 25, factory.CreateLogger("test"), TimeProvider.System, TestContext.Current.CancellationToken);

        available.ShouldBeNull();
        var record = logs.Collector.GetSnapshot().Single();
        record.Level.ShouldBe(LogLevel.Information);
        record.Message.ShouldStartWith("The connection budget check could not run");
    }

    [Fact]
    public async Task CheckBudgetAsync_ServerThatDoesNotAnswer_GivesUpAfterItsTimeout()
    {
        using var silent = new SilentTcpServer();
        using var logs = new FakeLoggerProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(logs));
        var clock = new FakeTimeProvider();
        // Npgsql's own timeouts are far away; only the check's deadline (on the fake clock) can end the wait.
        await using var dataSource = DependencyInjection.BuildDataSource(
            $"Host=127.0.0.1;Port={silent.Port};Username=x;Password=x;Timeout=20;Command Timeout=20", null);

        var check = NpgsqlConnectionPolicy.CheckBudgetAsync(dataSource, 25, factory.CreateLogger("test"), clock, TestContext.Current.CancellationToken);
        clock.Advance(NpgsqlConnectionPolicy.BudgetCheckTimeout);

        (await check.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).ShouldBeNull();
        logs.Collector.GetSnapshot().Single().Level.ShouldBe(LogLevel.Information);
    }

    [Fact]
    public async Task CheckBudgetAsync_CallersCancellation_Propagates()
    {
        using var silent = new SilentTcpServer();
        await using var dataSource = DependencyInjection.BuildDataSource(
            $"Host=127.0.0.1;Port={silent.Port};Username=x;Password=x;Timeout=20", null);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Should.ThrowAsync<OperationCanceledException>(() =>
            NpgsqlConnectionPolicy.CheckBudgetAsync(dataSource, 25, NullLogger.Instance, TimeProvider.System, cancel.Token));
    }

    [Fact]
    public async Task TickWriterDataSource_ClampsAMinimumPoolSizeAboveItsMaximum()
    {
        await using var writer = DependencyInjection.BuildTickWriterDataSource(db.RawConnectionString + ";Minimum Pool Size=10", null);

        var settings = new NpgsqlConnectionStringBuilder(writer.ConnectionString);
        settings.MaxPoolSize.ShouldBe(DependencyInjection.TickWriterMaxPoolSize);
        settings.MinPoolSize.ShouldBe(DependencyInjection.TickWriterMaxPoolSize);
        await using var command = writer.CreateCommand("SELECT 1");
        (await command.ExecuteScalarAsync(TestContext.Current.CancellationToken)).ShouldBe(1);
    }

    [Fact]
    public void Fixture_HelperConnections_FollowThePolicy_TheRawStringDoesNot()
    {
        var applied = new NpgsqlConnectionStringBuilder(db.ConnectionString);
        applied.GssEncryptionMode.ShouldBe(GssEncryptionMode.Disable);
        applied.MaxPoolSize.ShouldBe(NpgsqlConnectionPolicy.DefaultMaxPoolSize);

        var raw = new NpgsqlConnectionStringBuilder(db.RawConnectionString);
        raw.Keys.Cast<string>().ShouldNotContain("GSS Encryption Mode");
        raw.Keys.Cast<string>().ShouldNotContain("Maximum Pool Size");
    }

    [Fact]
    public async Task Startup_WithMigrationsOff_StillChecksTheConnectionBudget()
    {
        using var logs = new FakeLoggerProvider();
        await using var api = new ApiFactory(db)
            .WithSetting("ConnectionStrings:TickPulse", db.RawConnectionString)
            .WithSetting("Database:RunMigrationsOnStartup", "false")
            .WithSetting(NpgsqlConnectionPolicy.MaxPoolSizeKey, "95")
            .WithLogging(logs);
        await api.InitializeAsync();
        using var client = api.CreateClient();

        logs.Collector.GetSnapshot().ShouldContain(r =>
            r.Level == LogLevel.Warning && r.Message.StartsWith("The API may open 100 database connections", StringComparison.Ordinal));
    }

    [Fact]
    public void DesignTimeFactory_HasGssEncryptionDisabled()
    {
        using var context = new TickPulseDbContextDesignTimeFactory().CreateDbContext([]);

        GssMode(context.Database.GetDbConnection().ConnectionString).ShouldBe(GssEncryptionMode.Disable);
    }

    private static GssEncryptionMode GssMode(string connectionString) =>
        new NpgsqlConnectionStringBuilder(connectionString).GssEncryptionMode;

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
