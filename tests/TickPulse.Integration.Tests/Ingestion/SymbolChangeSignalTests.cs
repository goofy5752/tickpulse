using TickPulse.Infrastructure.Ingestion;

namespace TickPulse.Integration.Tests.Ingestion;

public sealed class SymbolChangeSignalTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Notify_ManyTimesBeforeWait_WakesOnce()
    {
        var signal = new SymbolChangeSignal();

        signal.Notify();
        signal.Notify();
        signal.Notify();

        await signal.WaitAsync(Ct).AsTask().WaitAsync(IngestionTestHost.WaitTimeout, Ct);
        using var cts = new CancellationTokenSource();
        var second = signal.WaitAsync(cts.Token).AsTask();
        second.IsCompleted.ShouldBeFalse();
        await cts.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(second);
    }

    [Fact]
    public async Task Notify_WhileWaiting_WakesTheWaiter()
    {
        var signal = new SymbolChangeSignal();
        var wait = signal.WaitAsync(Ct).AsTask();
        wait.IsCompleted.ShouldBeFalse();

        signal.Notify();

        await wait.WaitAsync(IngestionTestHost.WaitTimeout, Ct);
    }

    [Fact]
    public async Task WaitAsync_Cancelled_KeepsTheNotificationForTheNextWait()
    {
        var signal = new SymbolChangeSignal();
        using var cts = new CancellationTokenSource();
        var abandoned = signal.WaitAsync(cts.Token).AsTask();
        await cts.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(abandoned);

        signal.Notify();

        await signal.WaitAsync(Ct).AsTask().WaitAsync(IngestionTestHost.WaitTimeout, Ct);
    }
}
