using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using TickPulse.Application.Abstractions;
using TickPulse.Application.Ingestion;
using TickPulse.Infrastructure;
using TickPulse.Infrastructure.Binance;
using TickPulse.Integration.Tests.Fixtures;

namespace TickPulse.Integration.Tests.Ingestion;

/// <summary>
/// The ingestion worker hosted the way the API hosts it (<see cref="IHost"/> + <c>AddTickPulseInfrastructure</c> +
/// <c>AddTickPulseIngestion</c>), pointed at a <see cref="FakeBinanceServer"/> and the shared TimescaleDB.
/// </summary>
/// <remarks>
/// Everything runs on <see cref="TimeProvider.System"/>: the WebSocket throttle, the batch delay and the rotation
/// timer all need time to pass, and the tests await observable conditions with deadlines instead of sleeping.
/// </remarks>
internal sealed class IngestionTestHost : IAsyncDisposable
{
    public static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(20);

    private readonly IHost _host;
    private bool _stopped;

    private IngestionTestHost(IHost host, LogCapture logs)
    {
        _host = host;
        Logs = logs;
    }

    public IngestionState State => _host.Services.GetRequiredService<IngestionState>();

    public ISymbolChangeSignal Signal => _host.Services.GetRequiredService<ISymbolChangeSignal>();

    public ISymbolRepository Symbols => _host.Services.GetRequiredService<ISymbolRepository>();

    public LogCapture Logs { get; }

    public static async Task<IngestionTestHost> StartAsync(
        TimescaleFixture db,
        FakeBinanceServer server,
        IDictionary<string, string?>? overrides = null,
        Action<IServiceCollection>? configureServices = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:TickPulse"] = db.ConnectionString,
            ["Binance:WsBaseUrl"] = server.WsBaseUrl.ToString(),
            ["Binance:RestBaseUrl"] = server.RestBaseUrl.ToString(),
            ["Ingestion:BatchMaxDelay"] = "00:00:00.020",
            ["Ingestion:LeaderRetryInterval"] = "00:00:00.200",
            ["Ingestion:LeaderCheckInterval"] = "00:00:00.200",
        };
        foreach (var (key, value) in overrides ?? new Dictionary<string, string?>())
        {
            settings[key] = value;
        }

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(settings);
        var logs = new LogCapture();
        builder.Logging.AddProvider(logs);
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
        configureServices?.Invoke(builder.Services); // first, so the TryAdd registrations below keep it
        builder.Services.AddTickPulseInfrastructure(builder.Configuration);
        builder.Services.AddTickPulseIngestion(builder.Configuration);
        builder.Services.ConfigureBinanceRestResilience(o => o.Retry.Delay = TimeSpan.Zero);

        var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        return new IngestionTestHost(host, logs);
    }

    public async Task StopAsync()
    {
        if (!_stopped)
        {
            _stopped = true;
            await _host.StopAsync(CancellationToken.None);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        if (_host is IAsyncDisposable disposable)
        {
            await disposable.DisposeAsync();
        }
        else
        {
            _host.Dispose();
        }
    }

    /// <summary>Polls <paramref name="condition"/> until it holds; throws <see cref="TimeoutException"/> at the deadline.</summary>
    public static async Task WaitForAsync(Func<Task<bool>> condition, string what, TimeSpan? timeout = null)
    {
        var deadline = TimeProvider.System.GetTimestamp();
        var limit = timeout ?? WaitTimeout;
        while (!await condition())
        {
            if (TimeProvider.System.GetElapsedTime(deadline) > limit)
            {
                throw new TimeoutException($"Timed out waiting for: {what}");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), TestContext.Current.CancellationToken);
        }
    }

    public static Task WaitForAsync(Func<bool> condition, string what, TimeSpan? timeout = null) =>
        WaitForAsync(() => Task.FromResult(condition()), what, timeout);

    /// <summary>The current UTC time at millisecond precision, the precision of Binance trade times.</summary>
    public static DateTime Now
    {
        get
        {
            var now = TimeProvider.System.GetUtcNow().UtcDateTime;
            return now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMillisecond));
        }
    }

    public static string Stream(string symbol) => symbol.ToLowerInvariant() + "@aggTrade";

    /// <summary>The <c>agg_trade_id</c>s stored for a symbol, ascending (duplicates would show up twice).</summary>
    public static async Task<long[]> TradeIdsAsync(TimescaleFixture db, string symbol)
    {
        await using var connection = new NpgsqlConnection(db.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT t.agg_trade_id
              FROM price_ticks t JOIN symbols s ON s.id = t.symbol_id
             WHERE s.name = $1
             ORDER BY t.agg_trade_id
            """,
            connection);
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = symbol });
        var ids = new List<long>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            ids.Add(reader.GetInt64(0));
        }

        return [.. ids];
    }

    /// <summary>Sum of <c>tick_count</c> over a symbol's 1-minute candles: counts only ticks that were newly inserted.</summary>
    public static async Task<long> CandleTickCountAsync(TimescaleFixture db, string symbol)
    {
        await using var connection = new NpgsqlConnection(db.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT coalesce(sum(c.tick_count), 0)::bigint
              FROM candles_1m c JOIN symbols s ON s.id = c.symbol_id
             WHERE s.name = $1
            """,
            connection);
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = symbol });
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    public static async Task WaitForTradeCountAsync(TimescaleFixture db, string symbol, int count, TimeSpan? timeout = null) =>
        await WaitForAsync(
            async () => (await TradeIdsAsync(db, symbol)).Length >= count,
            string.Create(CultureInfo.InvariantCulture, $"{count} {symbol} ticks in the database"),
            timeout);

    /// <summary>Records every log entry so tests can await a specific event.</summary>
    internal sealed class LogCapture : ILoggerProvider
    {
        private readonly ConcurrentQueue<(string Category, LogLevel Level, string EventName, string Message)> _entries = new();

        public IReadOnlyList<(string Category, LogLevel Level, string EventName, string Message)> Entries => [.. _entries];

        public int Count(string eventName) => _entries.Count(e => e.EventName == eventName);

        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _entries);

        public void Dispose()
        {
        }

        private sealed class Logger(
            string category,
            ConcurrentQueue<(string Category, LogLevel Level, string EventName, string Message)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                entries.Enqueue((category, logLevel, eventId.Name ?? string.Empty, formatter(state, exception)));
        }
    }
}
