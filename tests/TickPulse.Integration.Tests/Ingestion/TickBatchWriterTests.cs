using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using TickPulse.Application.Abstractions;
using TickPulse.Application.Ingestion;
using TickPulse.Domain;
using TickPulse.Infrastructure.Ingestion;
using TickPulse.Infrastructure.Telemetry;

namespace TickPulse.Integration.Tests.Ingestion;

public sealed class TickBatchWriterTests : IDisposable
{
    private const int Btc = 1;
    private const int Eth = 3;
    private static readonly DateTime T0 = new(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(T0.AddSeconds(2)));
    private readonly Channel<Tick> _channel = Channel.CreateUnbounded<Tick>();
    private readonly RecordingTickWriter _writer = new();
    private readonly SymbolIdCache _symbols = new();
    private readonly RecordingWatermark _watermark = new();
    private readonly IngestionState _state;
    private readonly ServiceProvider _meters = new ServiceCollection().AddMetrics().BuildServiceProvider();

    public TickBatchWriterTests()
    {
        _state = new IngestionState(_clock);
        _symbols.Update(
        [
            new MonitoredSymbol(Btc, "BTCUSDT", true, T0, null, BackfillStatus.NotRequested, null),
            new MonitoredSymbol(Eth, "ETHUSDT", true, T0, null, BackfillStatus.NotRequested, null),
        ]);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _meters.Dispose();

    [Fact]
    public async Task Run_SizeReached_WritesFullBatches()
    {
        for (var i = 1; i <= 7; i++)
        {
            _channel.Writer.TryWrite(T(Btc, i));
        }

        _channel.Writer.Complete();

        await Writer(maxSize: 3).RunAsync(_channel.Reader, Ct);

        _writer.Batches.Select(b => b.Count).ShouldBe([3, 3, 1]);
        _writer.Batches.SelectMany(b => b).Select(t => t.AggTradeId).ShouldBe(Enumerable.Range(1, 7).Select(i => (long)i));
    }

    [Fact]
    public async Task Run_DelayElapsed_WritesPartialBatch()
    {
        var run = Writer(maxSize: 100).RunAsync(_channel.Reader, Ct);
        _channel.Writer.TryWrite(T(Btc, 1));
        _channel.Writer.TryWrite(T(Btc, 2));

        // Nothing is written until the batch delay passes on the clock.
        await AdvanceUntilAsync(() => _writer.Batches.Count == 1);
        _writer.Batches[0].Count.ShouldBe(2);

        _channel.Writer.Complete();
        await run.WaitAsync(IngestionTestHost.WaitTimeout, Ct);
        _writer.Batches.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Run_StoredBatch_UpdatesStateWithTheLastPricePerSymbol()
    {
        var updates = new ConcurrentQueue<LivePrice>();
        _state.PriceUpdated += updates.Enqueue;
        _state.PriceUpdated += _ => throw new InvalidOperationException("a broken subscriber");
        _channel.Writer.TryWrite(T(Btc, 2, 102m, ms: 500));
        _channel.Writer.TryWrite(T(Btc, 1, 101m, ms: 900)); // older id, delivered later
        _channel.Writer.TryWrite(T(Eth, 7, 3000m, ms: 1500));
        _channel.Writer.Complete();

        await Writer().RunAsync(_channel.Reader, Ct);

        _state.Latest["BTCUSDT"].ShouldBe(new LivePrice("BTCUSDT", 102m, T0.AddMilliseconds(500)));
        _state.Latest["ETHUSDT"].ShouldBe(new LivePrice("ETHUSDT", 3000m, T0.AddMilliseconds(1500)));
        updates.Count.ShouldBe(2);
        _state.LastLagMs.ShouldBe(500); // now (T0 + 2 s) minus the newest trade (T0 + 1.5 s)
        _state.TicksPerSecond.ShouldBe(3 / IngestionState.RateWindow.TotalSeconds, 1e-9);
    }

    [Fact]
    public async Task Run_RecoveredBatch_RecordsNoLagSample()
    {
        // A streamed batch (traded 500 ms ago), then a gap-fill batch: a trade from a minute ago, received just now.
        _channel.Writer.TryWrite(T(Btc, 1, 101m, ms: 1500));
        _channel.Writer.TryWrite(new Tick(Btc, 2, 99m, 1m, T0.AddMinutes(-1), T0.AddSeconds(2)));
        _channel.Writer.Complete();

        await Writer(maxSize: 1).RunAsync(_channel.Reader, Ct);

        _writer.Batches.Count.ShouldBe(2);
        _state.LastLagMs.ShouldBe(500); // the streamed batch's lag, not the recovered trade's age
        _state.TicksPerSecond.ShouldBe(2 / IngestionState.RateWindow.TotalSeconds, 1e-9); // both still count as stored
    }

    [Theory]
    [InlineData(PostgresErrorCodes.DeadlockDetected)]
    [InlineData(PostgresErrorCodes.SerializationFailure)]
    public async Task Run_TransientConflict_RetriesTheBatch(string sqlState)
    {
        _writer.Failures.Enqueue(new PostgresException("conflict", "ERROR", "ERROR", sqlState));
        _channel.Writer.TryWrite(T(Btc, 1));
        _channel.Writer.Complete();

        var run = Writer().RunAsync(_channel.Reader, Ct);
        await AdvanceUntilAsync(() => run.IsCompleted);
        await run;

        _writer.Attempts.ShouldBe(2);
        _writer.Batches.ShouldHaveSingleItem().Count.ShouldBe(1);
        _state.Latest.ShouldContainKey("BTCUSDT");
    }

    [Fact]
    public async Task Run_PersistentConflict_GivesUpAfterMaxAttempts()
    {
        for (var i = 0; i < TickBatchWriter.MaxConflictAttempts; i++)
        {
            _writer.Failures.Enqueue(new PostgresException("deadlock", "ERROR", "ERROR", PostgresErrorCodes.DeadlockDetected));
        }

        _channel.Writer.TryWrite(T(Btc, 1));
        _channel.Writer.Complete();

        var run = Writer().RunAsync(_channel.Reader, Ct);
        await AdvanceUntilAsync(() => run.IsCompleted);
        await run;

        _writer.Attempts.ShouldBe(TickBatchWriter.MaxConflictAttempts);
        _writer.Batches.ShouldBeEmpty();
        _state.Latest.ShouldBeEmpty();
        _watermark.Lowered.ShouldBe([("BTCUSDT", 1L)]);
    }

    [Fact]
    public async Task Run_OtherFailure_DropsTheBatchAndContinues()
    {
        _writer.Failures.Enqueue(new InvalidOperationException("broken"));
        _channel.Writer.TryWrite(T(Btc, 1));
        _channel.Writer.TryWrite(T(Btc, 2));
        _channel.Writer.Complete();

        await Writer(maxSize: 1).RunAsync(_channel.Reader, Ct);

        _writer.Attempts.ShouldBe(2);
        _writer.Batches.ShouldHaveSingleItem().ShouldHaveSingleItem().AggTradeId.ShouldBe(2);
        _watermark.Lowered.ShouldBe([("BTCUSDT", 1L)]);
    }

    [Fact]
    public async Task Run_TransientFailures_RetriedUntilStoredWithNothingLost()
    {
        // The database restarts: refused connections, an admin shutdown, a broken connection, a timeout.
        _writer.Failures.Enqueue(new NpgsqlException("Failed to connect", new System.Net.Sockets.SocketException(10061)));
        _writer.Failures.Enqueue(new PostgresException("terminating connection due to administrator command", "FATAL", "FATAL", PostgresErrorCodes.AdminShutdown));
        _writer.Failures.Enqueue(new PostgresException("the database system is starting up", "FATAL", "FATAL", PostgresErrorCodes.CannotConnectNow));
        _writer.Failures.Enqueue(new PostgresException("connection failure", "FATAL", "FATAL", PostgresErrorCodes.ConnectionFailure));
        _writer.Failures.Enqueue(new NpgsqlException("Exception while reading from stream", new IOException("reset")));
        _writer.Failures.Enqueue(new TimeoutException());
        for (var i = 1; i <= 5; i++)
        {
            _channel.Writer.TryWrite(T(Btc, i));
        }

        _channel.Writer.Complete();

        var run = Writer().RunAsync(_channel.Reader, Ct);
        await AdvanceUntilAsync(() => run.IsCompleted, step: TimeSpan.FromSeconds(1));
        await run;

        _writer.Attempts.ShouldBe(7);
        _writer.Batches.ShouldHaveSingleItem().Select(t => t.AggTradeId).ShouldBe([1L, 2L, 3L, 4L, 5L]);
        _watermark.Lowered.ShouldBeEmpty();
    }

    [Fact]
    public async Task Run_TransientFailures_BackOffUpToTheCap()
    {
        for (var i = 0; i < 12; i++)
        {
            _writer.Failures.Enqueue(new TimeoutException());
        }

        _channel.Writer.TryWrite(T(Btc, 1));
        _channel.Writer.Complete();
        var started = _clock.GetUtcNow();

        var run = Writer().RunAsync(_channel.Reader, Ct);
        await AdvanceUntilAsync(() => run.IsCompleted, step: TimeSpan.FromSeconds(1));
        await run;

        // 0.25, 0.5, 1, 2, 4, 8, 16, then 30 s capped five times: at least 181.75 s of waiting in total.
        (_clock.GetUtcNow() - started).ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(181.75));
        (_clock.GetUtcNow() - started).ShouldBeLessThan(TimeSpan.FromSeconds(181.75 + 60)); // uncapped it would be over 1,100 s
        _writer.Batches.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Run_PermanentFailure_LowersTheWatermarkPerSymbol()
    {
        _writer.Failures.Enqueue(new PostgresException("value too long", "ERROR", "ERROR", PostgresErrorCodes.StringDataRightTruncation));
        _channel.Writer.TryWrite(T(Btc, 12));
        _channel.Writer.TryWrite(T(Btc, 10));
        _channel.Writer.TryWrite(T(Eth, 7));
        _channel.Writer.TryWrite(T(Btc, 11));
        _channel.Writer.Complete();

        await Writer().RunAsync(_channel.Reader, Ct);

        _writer.Attempts.ShouldBe(1);
        _writer.Batches.ShouldBeEmpty();
        _watermark.Lowered.ShouldBe([("BTCUSDT", 10L), ("ETHUSDT", 7L)], ignoreOrder: true);
    }

    [Fact]
    public async Task Run_Abandoned_RecordsUnwrittenTicksAsAGap()
    {
        for (var i = 0; i < 100; i++)
        {
            _writer.Failures.Enqueue(new TimeoutException()); // the database stays down
        }

        _channel.Writer.TryWrite(T(Btc, 5));
        _channel.Writer.TryWrite(T(Btc, 6));
        using var abandon = new CancellationTokenSource();
        var run = Writer(maxSize: 1).RunAsync(_channel.Reader, abandon.Token);
        await IngestionTestHost.WaitForAsync(() => _writer.Attempts >= 1, "the first attempt");
        _channel.Writer.TryWrite(T(Eth, 3));

        await abandon.CancelAsync();
        await run.WaitAsync(IngestionTestHost.WaitTimeout, Ct);

        _writer.Batches.ShouldBeEmpty();
        _watermark.Lowered.ShouldBe([("BTCUSDT", 5L), ("ETHUSDT", 3L)], ignoreOrder: true);
    }

    [Fact]
    public async Task Run_InactiveSymbol_NotPublishedAsLivePrice()
    {
        _symbols.Update([new MonitoredSymbol(Btc, "BTCUSDT", true, T0, null, BackfillStatus.NotRequested, null)]); // ETH removed
        _channel.Writer.TryWrite(T(Btc, 1));
        _channel.Writer.TryWrite(T(Eth, 2));
        _channel.Writer.Complete();

        await Writer().RunAsync(_channel.Reader, Ct);

        _state.Latest.Keys.ShouldBe(["BTCUSDT"]);
        _writer.Batches.ShouldHaveSingleItem().Count.ShouldBe(2); // its in-flight ticks are still stored
    }

    private static Tick T(int symbolId, long id, decimal price = 100m, int ms = 0) =>
        new(symbolId, id, price, 1m, T0.AddMilliseconds(ms), T0.AddMilliseconds(ms));

    private TickBatchWriter Writer(int maxSize = 5000) => new(
        _writer,
        _state,
        _symbols,
        _watermark,
        Options.Create(new IngestionOptions { BatchMaxSize = maxSize, BatchMaxDelay = TimeSpan.FromMilliseconds(500) }),
        _clock,
        NullLogger<TickBatchWriter>.Instance,
        new TickPulseMetrics(_meters.GetRequiredService<IMeterFactory>(), _state, _symbols));

    // Timers on the fake clock are registered asynchronously, so keep nudging it until the condition holds.
    private Task AdvanceUntilAsync(Func<bool> condition, TimeSpan? step = null) =>
        IngestionTestHost.WaitForAsync(
            () =>
            {
                if (condition())
                {
                    return true;
                }

                _clock.Advance(step ?? TimeSpan.FromMilliseconds(100));
                return condition();
            },
            "the fake clock to release the writer");

    private sealed class RecordingWatermark : IGapFillWatermark
    {
        private readonly ConcurrentQueue<(string Symbol, long FirstMissingId)> _lowered = new();

        public IReadOnlyList<(string Symbol, long FirstMissingId)> Lowered => [.. _lowered];

        public void Lower(string symbol, long firstMissingId) => _lowered.Enqueue((symbol, firstMissingId));
    }

    private sealed class RecordingTickWriter : ITickWriter
    {
        private readonly ConcurrentQueue<IReadOnlyList<Tick>> _batches = new();
        private int _attempts;

        public ConcurrentQueue<Exception> Failures { get; } = new();

        public IReadOnlyList<IReadOnlyList<Tick>> Batches => [.. _batches];

        public int Attempts => Volatile.Read(ref _attempts);

        public Task<int> WriteBatchAsync(IReadOnlyList<Tick> ticks, IDictionary<int, int> insertedPerSymbol, CancellationToken ct)
        {
            Interlocked.Increment(ref _attempts);
            if (Failures.TryDequeue(out var failure))
            {
                throw failure;
            }

            _batches.Enqueue([.. ticks]);
            insertedPerSymbol.Clear();
            foreach (var group in ticks.GroupBy(t => t.SymbolId))
            {
                insertedPerSymbol[group.Key] = group.Count();
            }

            return Task.FromResult(ticks.Count);
        }
    }
}
