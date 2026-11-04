using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using TickPulse.Api.Hosting;

namespace TickPulse.Integration.Tests.Api;

public sealed class DatabaseMigratorTests
{
    private static readonly MigrationRetryOptions NoDelay = new(MaxAttempts: 5, BaseDelay: TimeSpan.Zero, Timeout: TimeSpan.FromMinutes(2));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static NpgsqlException Transient() => new("connection refused", new SocketException((int)SocketError.ConnectionRefused));

    [Fact]
    public async Task Migrate_TransientFailuresThenSuccess_Retries()
    {
        var calls = 0;

        await DatabaseMigrator.RunWithRetryAsync(
            _ => ++calls < 3 ? throw Transient() : Task.CompletedTask,
            NoDelay,
            new FakeTimeProvider(),
            NullLogger.Instance,
            Ct);

        calls.ShouldBe(3);
    }

    [Fact]
    public async Task Migrate_AlwaysTransient_GivesUpAfterMaxAttempts()
    {
        var calls = 0;

        await Should.ThrowAsync<NpgsqlException>(() => DatabaseMigrator.RunWithRetryAsync(
            _ =>
            {
                calls++;
                throw Transient();
            },
            NoDelay,
            new FakeTimeProvider(),
            NullLogger.Instance,
            Ct));

        calls.ShouldBe(5);
    }

    [Fact]
    public async Task Migrate_NonTransientFailure_DoesNotRetry()
    {
        var calls = 0;

        await Should.ThrowAsync<NpgsqlException>(() => DatabaseMigrator.RunWithRetryAsync(
            _ =>
            {
                calls++;
                throw new NpgsqlException("syntax error");
            },
            NoDelay,
            new FakeTimeProvider(),
            NullLogger.Instance,
            Ct));

        calls.ShouldBe(1);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(4, 8)]
    public void RetryDelay_DoublesPerAttempt(int failedAttempts, int expectedSeconds) =>
        DatabaseMigrator.RetryDelay(TimeSpan.FromSeconds(1), failedAttempts).ShouldBe(TimeSpan.FromSeconds(expectedSeconds));

    [Fact]
    public async Task Migrate_SlowerThanTimeout_ThrowsTimeout()
    {
        var clock = new FakeTimeProvider();
        var options = NoDelay with { Timeout = TimeSpan.FromSeconds(30) };

        var run = DatabaseMigrator.RunWithRetryAsync(
            token => Task.Delay(Timeout.InfiniteTimeSpan, token),
            options,
            clock,
            NullLogger.Instance,
            Ct);
        clock.Advance(TimeSpan.FromSeconds(30));

        (await Should.ThrowAsync<TimeoutException>(run)).Message.ShouldContain("30");
    }

    [Fact]
    public async Task Migrate_HostStopping_ThrowsCanceled()
    {
        using var stopping = new CancellationTokenSource();

        var run = DatabaseMigrator.RunWithRetryAsync(
            token => Task.Delay(Timeout.InfiniteTimeSpan, token),
            NoDelay,
            new FakeTimeProvider(),
            NullLogger.Instance,
            stopping.Token);
        await stopping.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(run);
    }
}
