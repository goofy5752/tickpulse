using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using TickPulse.Infrastructure.Backfill;
using TickPulse.Infrastructure.Binance;
using TickPulse.Infrastructure.Ingestion;

namespace TickPulse.Integration.Tests.Fixtures;

/// <summary>
/// The real API host (<c>Program</c>) on a <see cref="TestServer"/>, wired to the shared TimescaleDB, a
/// <see cref="FakeBinanceServer"/>, <see cref="AdminApiKey"/> and a <see cref="FakeTimeProvider"/>
/// (<see cref="Clock"/>). Use it as a class fixture, or create one per test when it needs its own settings:
/// <c>await using var api = new ApiFactory(db).WithSetting(...); await api.InitializeAsync();</c>.
/// </summary>
/// <remarks>
/// <para>The hosted ingestion and backfill workers are removed unless <see cref="WithWorkers"/> is called, because they
/// would race <see cref="TimescaleFixture.ResetAsync"/>. Startup migrations still run (a no-op on the fixture's
/// database).</para>
/// <para>The fake clock is the app's <see cref="TimeProvider"/> unless <see cref="WithSystemClock"/> is called. The
/// Binance REST retries then have no delay, so they never wait on the frozen clock. The WebSocket connections (and so
/// their subscribe throttle) always run on <see cref="TimeProvider.System"/>, so commands are never held by a frozen
/// clock. Workers that batch or retry on the app clock need <see cref="WithSystemClock"/>.</para>
/// <para>The web root is a temporary directory (<see cref="WebRoot"/>), empty until a test writes files into it, or a
/// path that does not exist after <see cref="WithoutWebRoot"/>. The development static web assets manifest
/// (<c>TickPulse.Api.staticwebassets.runtime.json</c>) is never loaded: an incremental build keeps it after a local
/// <c>wwwroot</c> is deleted, and the host would then fail to start because a content root it names is gone.</para>
/// <para>Test-only controllers in this assembly (under <c>api/probe</c>) are added as an application part.</para>
/// </remarks>
public sealed class ApiFactory(TimescaleFixture db) : WebApplicationFactory<Program>, IAsyncLifetime
{
    /// <summary>The fixture's admin key: 36 characters, so the host also starts in the Production environment.</summary>
    public const string AdminApiKey = "test-admin-key-0123456789abcdef-0123";

    private readonly Dictionary<string, string?> _settings = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Action<IServiceCollection>> _services = [];
    private bool _workers;
    private bool _systemClock;
    private bool _configured;
    private string? _environment;
    private bool _noWebRoot;
    private ILoggerProvider? _logs;

    /// <summary>The app's clock, starting at the current time (millisecond precision); frozen until a test moves it.</summary>
    public FakeTimeProvider Clock { get; } = new(StartTime());

    public FakeBinanceServer Binance { get; } = new();

    public TimescaleFixture Database => db;

    /// <summary>The host's web root, a temporary directory deleted with the factory.</summary>
    public string WebRoot { get; } = Directory.CreateTempSubdirectory("tickpulse-wwwroot-").FullName;

    /// <summary>Keeps the hosted ingestion and backfill workers. Call before the host starts.</summary>
    public ApiFactory WithWorkers()
    {
        EnsureNotConfigured();
        _workers = true;
        return this;
    }

    /// <summary>Leaves <see cref="TimeProvider.System"/> as the app's clock; <see cref="Clock"/> is then unused.</summary>
    public ApiFactory WithSystemClock()
    {
        EnsureNotConfigured();
        _systemClock = true;
        return this;
    }

    /// <summary>Points the web root at a directory that does not exist, as on a machine without a dashboard build.</summary>
    public ApiFactory WithoutWebRoot()
    {
        EnsureNotConfigured();
        _noWebRoot = true;
        return this;
    }

    /// <summary>Runs the host in another environment than <c>Development</c> (the factory's default).</summary>
    public ApiFactory WithEnvironment(string environment)
    {
        EnsureNotConfigured();
        _environment = environment;
        return this;
    }

    /// <summary>Also sends the host's logs to <paramref name="provider"/>. Call before the host starts.</summary>
    public ApiFactory WithLogging(ILoggerProvider provider)
    {
        EnsureNotConfigured();
        _logs = provider;
        return this;
    }

    /// <summary>Adjusts the app's services after every other registration (e.g. replaces one with a test double). Call before the host starts.</summary>
    public ApiFactory WithServices(Action<IServiceCollection> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        EnsureNotConfigured();
        _services.Add(configure);
        return this;
    }

    /// <summary>Overrides one configuration value. Call before the host starts.</summary>
    public ApiFactory WithSetting(string key, string? value)
    {
        EnsureNotConfigured();
        _settings[key] = value;
        return this;
    }

    public async ValueTask InitializeAsync() => await Binance.InitializeAsync();

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await Binance.DisposeAsync();
        try
        {
            Directory.Delete(WebRoot, recursive: true);
        }
        catch (IOException)
        {
            // A temp directory left behind is harmless.
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        _configured = true;
        if (_environment is not null)
        {
            builder.UseEnvironment(_environment);
        }

        builder.UseWebRoot(_noWebRoot ? Path.Combine(WebRoot, "missing") : WebRoot);

        // A path with no file: the development static web assets loader then has nothing to load (see the remarks).
        builder.UseSetting(WebHostDefaults.StaticWebAssetsKey, Path.Combine(WebRoot, "no-static-web-assets.json"));
        if (_logs is not null)
        {
            builder.ConfigureLogging(logging => logging.AddProvider(_logs));
        }

        builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(Settings()));
        builder.ConfigureTestServices(services =>
        {
            if (!_systemClock)
            {
                services.Replace(ServiceDescriptor.Singleton<TimeProvider>(Clock));
                services.ConfigureBinanceRestResilience(o => o.Retry.Delay = TimeSpan.Zero);
            }

            services.Replace(ServiceDescriptor.Singleton<IStreamConnectionFactory>(sp => new StreamConnectionFactory(
                Binance.WsBaseUrl,
                TimeProvider.System,
                sp.GetRequiredService<ILoggerFactory>())));

            if (!_workers)
            {
                RemoveWorkers(services);
            }

            // Through the existing part manager: calling AddControllers() again would re-register MVC's own problem
            // writer, so the test host's writers would differ from production's.
            var parts = services.Single(d => d.ServiceType == typeof(ApplicationPartManager)).ImplementationInstance;
            ((ApplicationPartManager)parts!).ApplicationParts.Add(new AssemblyPart(typeof(ApiFactory).Assembly));

            foreach (var configure in _services)
            {
                configure(services);
            }
        });
    }

    private Dictionary<string, string?> Settings()
    {
        var settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["ConnectionStrings:TickPulse"] = db.ConnectionString,
            ["Binance:WsBaseUrl"] = Binance.WsBaseUrl.ToString(),
            ["Binance:RestBaseUrl"] = Binance.RestBaseUrl.ToString(),
            ["Admin:ApiKey"] = AdminApiKey,
            ["Backfill:Enabled"] = "false",
            ["Ingestion:BatchMaxDelay"] = "00:00:00.020",
            ["Ingestion:LeaderRetryInterval"] = "00:00:00.200",
            ["Ingestion:LeaderCheckInterval"] = "00:00:00.200",
        };
        foreach (var (key, value) in _settings)
        {
            settings[key] = value;
        }

        return settings;
    }

    private static void RemoveWorkers(IServiceCollection services)
    {
        var workers = services
            .Where(d => d.ServiceType == typeof(IHostedService) && !d.IsKeyedService)
            .Where(d => ImplementationType(d) is { } type && (type == typeof(IngestionWorker) || type == typeof(BackfillWorker)))
            .ToList();

        // Both are registered by AddTickPulseIngestion; finding fewer means the registration changed and a worker
        // would silently keep running under the tests.
        if (workers.Count != 2)
        {
            throw new InvalidOperationException($"Expected the ingestion and backfill hosted services, found {workers.Count}.");
        }

        foreach (var worker in workers)
        {
            services.Remove(worker);
        }
    }

    // A factory registration (AddHostedService(sp => new X(...))) keeps its delegate's static return type.
    private static Type? ImplementationType(ServiceDescriptor descriptor) =>
        descriptor.ImplementationType
        ?? descriptor.ImplementationInstance?.GetType()
        ?? descriptor.ImplementationFactory?.GetType().GenericTypeArguments[^1];

    private static DateTimeOffset StartTime()
    {
        var now = TimeProvider.System.GetUtcNow();
        return now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMillisecond));
    }

    private void EnsureNotConfigured()
    {
        if (_configured)
        {
            throw new InvalidOperationException("Configure the factory before its host starts.");
        }
    }
}
