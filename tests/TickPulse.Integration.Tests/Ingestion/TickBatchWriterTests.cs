using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using TickPulse.Application.Abstractions;
using TickPulse.Application.Ingestion;
using TickPulse.Domain;
using TickPulse.Infrastructure.Ingestion;

namespace TickPulse.Integration.Tests.Ingestion;

public sealed class TickBatchWriterTests
{
    private const int Btc = 1;
    private const int Eth = 3;
    private static readonly DateTime T0 = new(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(T0.AddSeconds(2)));
    private readonly Channel<Tick> _channel = Channel.CreateUnbounded<Tick>();
    private readonly RecordingTickWriter _writer = new();
    private readonly SymbolIdCache _symbols = new();
    private readonly IngestionState _state;

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
        for (var i = 0; i < TickBatchWriter.MaxAttempts; i++)
        {
            _writer.Failures.Enqueue(new PostgresException("deadlock", "ERROR", "ERROR", PostgresErrorCodes.DeadlockDetected));
        }

        _channel.Writer.TryWrite(T(Btc, 1));
        _channel.Writer.Complete();

        var run = Writer().RunAsync(_channel.Reader, Ct);
        await AdvanceUntilAsync(() => run.IsCompleted);
        await run;

        _writer.Attempts.ShouldBe(TickBatchWriter.MaxAttempts);
        _writer.Batches.ShouldBeEmpty();
        _state.Latest.ShouldBeEmpty();
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
    }

    private static Tick T(int symbolId, long id, decimal price = 100m, int ms = 0) =>
        new(symbolId, id, price, 1m, T0.AddMilliseconds(ms), T0.AddMilliseconds(ms));

    private TickBatchWriter Writer(int maxSize = 5000) => new(
        _writer,
        _state,
        _symbols,
        Options.Create(new IngestionOptions { BatchMaxSize = maxSize, BatchMaxDelay = TimeSpan.FromMilliseconds(500) }),
        _clock,
        NullLogger<TickBatchWriter>.Instance);

    // Timers on the fake clock are registered asynchronously, so keep nudging it until the condition holds.
    private Task AdvanceUntilAsync(Func<bool> condition) =>
        IngestionTestHost.WaitForAsync(
            () =>
            {
                if (condition())
                {
                    return true;
                }

                _clock.Advance(TimeSpan.FromMilliseconds(100));
                return condition();
            },
            "the fake clock to release the writer");

    private sealed class RecordingTickWriter : ITickWriter
    {
        private readonly ConcurrentQueue<IReadOnlyList<Tick>> _batches = new();
        private int _attempts;

        public ConcurrentQueue<Exception> Failures { get; } = new();

        public IReadOnlyList<IReadOnlyList<Tick>> Batches => [.. _batches];

        public int Attempts => Volatile.Read(ref _attempts);

        public Task<int> WriteBatchAsync(IReadOnlyList<Tick> ticks, CancellationToken ct)
        {
            Interlocked.Increment(ref _attempts);
            if (Failures.TryDequeue(out var failure))
            {
                throw failure;
            }

            _batches.Enqueue([.. ticks]);
            return Task.FromResult(ticks.Count);
        }
    }
}
