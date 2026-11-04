using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;
using Serilog.Events;
using TickPulse.Api.Telemetry;

namespace TickPulse.Integration.Tests.Telemetry;

/// <summary>
/// The request sample budget and the request log levels: a burst of ordinary requests is logged (and traced) only up
/// to the budget, while problems and slow requests are always logged.
/// </summary>
public sealed class RequestSamplingTests
{
    private static readonly Func<bool> Sampled = () => true;
    private static readonly Func<bool> NotSampled = () => false;

    [Fact]
    public void Budget_StartsFull_ThenRefusesWithinTheSameSecond()
    {
        var budget = new RequestSampleBudget(10, new FakeTimeProvider());

        Taken(budget, 100).ShouldBe(10);
    }

    [Fact]
    public void Budget_RefillsAtTheRate_UpToOneSecondsWorth()
    {
        var clock = new FakeTimeProvider();
        var budget = new RequestSampleBudget(10, clock);
        Taken(budget, 10);

        clock.Advance(TimeSpan.FromMilliseconds(500));
        Taken(budget, 100).ShouldBe(5);

        // A quiet minute does not bank more than one second's worth for the next burst.
        clock.Advance(TimeSpan.FromMinutes(1));
        Taken(budget, 100).ShouldBe(10);
    }

    [Fact]
    public void Budget_SustainedBurst_KeepsTheRatePerSecond()
    {
        var clock = new FakeTimeProvider();
        var budget = new RequestSampleBudget(10, clock);

        // 5,000 requests per second for 10 seconds, one every 0.2 ms.
        var kept = 0;
        for (var i = 0; i < 50_000; i++)
        {
            kept += budget.TryTake() ? 1 : 0;
            clock.Advance(TimeSpan.FromTicks(2_000));
        }

        kept.ShouldBeInRange(105, 111); // the initial 10 plus 10 per second
    }

    [Fact]
    public void Budget_BelowOnePerSecond_StillKeepsOneAtATime()
    {
        var clock = new FakeTimeProvider();
        var budget = new RequestSampleBudget(0.5, clock);

        Taken(budget, 10).ShouldBe(1);
        clock.Advance(TimeSpan.FromSeconds(1));
        Taken(budget, 10).ShouldBe(0);
        clock.Advance(TimeSpan.FromSeconds(1));
        Taken(budget, 10).ShouldBe(1);
    }

    [Fact]
    public void Budget_Zero_KeepsNothing()
    {
        var clock = new FakeTimeProvider();
        var budget = new RequestSampleBudget(0, clock);

        Taken(budget, 10).ShouldBe(0);
        clock.Advance(TimeSpan.FromMinutes(1));
        Taken(budget, 10).ShouldBe(0);
    }

    [Fact]
    public void Budget_Negative_Throws() =>
        Should.Throw<ArgumentOutOfRangeException>(() => new RequestSampleBudget(-1, new FakeTimeProvider()));

    [Theory]
    [InlineData(null, 10d)]
    [InlineData("", 10d)]
    [InlineData("25", 25d)]
    [InlineData("0.5", 0.5d)]
    [InlineData("0", 0d)]
    public void PerSecond_ReadsTheSetting(string? value, double expected) =>
        RequestSampleBudget.PerSecond(Config(value)).ShouldBe(expected);

    [Theory]
    [InlineData("-1")]
    [InlineData("ten")]
    [InlineData("Infinity")]
    public void PerSecond_Invalid_Throws(string value) =>
        Should.Throw<InvalidOperationException>(() => RequestSampleBudget.PerSecond(Config(value))).Message.ShouldContain("Otel:SampledRequestsPerSecond");

    [Theory]
    [InlineData("/api/BTCUSDT/24hAvgPrice", 200)]
    [InlineData("/api/symbols", 304)]
    [InlineData("/assets/index.js", 200)]
    [InlineData("/api/BTCUSDT/24hAvgPrice", 429)] // expected under load; the metrics count every rejection
    public void OrdinaryRequest_FollowsTheSample(string path, int status)
    {
        Level(path, status, sampled: Sampled).ShouldBe(LogEventLevel.Information);
        Level(path, status, sampled: NotSampled).ShouldBe(LogEventLevel.Debug);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(404)]
    [InlineData(406)]
    public void ClientError_IsAlwaysInformation(int status) =>
        Level("/api/NOPEUSDT/24hAvgPrice", status, sampled: NotSampled).ShouldBe(LogEventLevel.Information);

    [Theory]
    [InlineData(500)]
    [InlineData(503)]
    public void ServerError_IsAlwaysError(int status) =>
        Level("/api/BTCUSDT/24hAvgPrice", status, sampled: NotSampled).ShouldBe(LogEventLevel.Error);

    [Fact]
    public void Exception_IsError_EvenOnHealth()
    {
        RequestLogPolicy.Level("/api/symbols", 200, 1, new InvalidOperationException(), NotSampled).ShouldBe(LogEventLevel.Error);
        RequestLogPolicy.Level("/health/ready", 200, 1, new InvalidOperationException(), NotSampled).ShouldBe(LogEventLevel.Error);
    }

    [Theory]
    [InlineData(250, LogEventLevel.Information)]
    [InlineData(10_000, LogEventLevel.Information)] // e.g. a dashboard's SignalR connection closing
    [InlineData(249.9, LogEventLevel.Debug)]
    public void SlowRequest_IsAlwaysLogged(double elapsedMs, LogEventLevel expected) =>
        Level("/api/BTCUSDT/SimpleMovingAverage", 200, elapsedMs, NotSampled).ShouldBe(expected);

    [Theory]
    [InlineData("/health/ready", 200, LogEventLevel.Debug)]
    [InlineData("/health/live", 200, LogEventLevel.Debug)]
    [InlineData("/health/ready", 503, LogEventLevel.Warning)] // not ready yet: expected while ingestion connects
    public void Health_IsDebug_NotReadyIsWarning(string path, int status, LogEventLevel expected)
    {
        Level(path, status, sampled: Sampled).ShouldBe(expected);
        Level(path, status, elapsedMs: 5_000, sampled: Sampled).ShouldBe(expected);
    }

    [Fact]
    public void Problems_DoNotTakeFromTheBudget()
    {
        var calls = 0;
        bool Counting() => ++calls > 0;

        Level("/api/NOPEUSDT/24hAvgPrice", 404, sampled: Counting);
        Level("/api/BTCUSDT/24hAvgPrice", 500, sampled: Counting);
        Level("/api/BTCUSDT/24hAvgPrice", 200, elapsedMs: 300, sampled: Counting);
        Level("/health/ready", 200, sampled: Counting);
        calls.ShouldBe(0);

        Level("/api/BTCUSDT/24hAvgPrice", 200, sampled: Counting);
        calls.ShouldBe(1);
    }

    [Fact]
    public void KeepTrace_MarksARecordedOnlySpanAsSampled()
    {
        using var activity = new Activity("request") { ActivityTraceFlags = ActivityTraceFlags.None, IsAllDataRequested = true };

        RequestLogPolicy.KeepTrace(activity);

        activity.Recorded.ShouldBeTrue();
    }

    [Fact]
    public void KeepTrace_LeavesAnUntracedSpanAlone()
    {
        // e.g. a health probe, which the ASP.NET Core instrumentation filters out
        using var activity = new Activity("request") { ActivityTraceFlags = ActivityTraceFlags.None, IsAllDataRequested = false };

        RequestLogPolicy.KeepTrace(activity);
        RequestLogPolicy.KeepTrace(null);

        activity.Recorded.ShouldBeFalse();
    }

    private static LogEventLevel Level(string path, int status, double elapsedMs = 5, Func<bool>? sampled = null) =>
        RequestLogPolicy.Level(new PathString(path), status, elapsedMs, exception: null, sampled ?? NotSampled);

    private static int Taken(RequestSampleBudget budget, int attempts) =>
        Enumerable.Range(0, attempts).Count(_ => budget.TryTake());

    private static IConfiguration Config(string? value) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [RequestSampleBudget.PerSecondKey] = value }).Build();
}
