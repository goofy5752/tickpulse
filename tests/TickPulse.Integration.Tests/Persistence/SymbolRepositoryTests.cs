using Microsoft.Extensions.Time.Testing;
using Npgsql;
using TickPulse.Application.Abstractions;
using TickPulse.Domain;
using TickPulse.Infrastructure.Persistence;
using TickPulse.Integration.Tests.Fixtures;

namespace TickPulse.Integration.Tests.Persistence;

public sealed class SymbolRepositoryTests(TimescaleFixture db) : IAsyncLifetime
{
    private const int Limit = 20;

    private static readonly DateTimeOffset Now = new(2026, 5, 4, 12, 30, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _clock = new(Now);
    private NpgsqlDataSource _dataSource = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await db.ResetAsync();
        _dataSource = NpgsqlDataSource.Create(db.ConnectionString);
    }

    public async ValueTask DisposeAsync() => await _dataSource.DisposeAsync();

    [Fact]
    public async Task AddOrReactivate_New_ReturnsCreated()
    {
        var outcome = await Repository().AddOrReactivateAsync(Name("SOLUSDT"), Limit, Ct);

        outcome.Status.ShouldBe(SymbolAddStatus.Created);
        var symbol = outcome.Symbol.ShouldNotBeNull();
        symbol.Name.ShouldBe("SOLUSDT");
        symbol.IsActive.ShouldBeTrue();
        symbol.CreatedAtUtc.ShouldBe(Now.UtcDateTime);
        symbol.CreatedAtUtc.Kind.ShouldBe(DateTimeKind.Utc);
        symbol.DeactivatedAtUtc.ShouldBeNull();
        symbol.BackfillStatus.ShouldBe(BackfillStatus.NotRequested);
        (await Repository().FindAsync(Name("SOLUSDT"), Ct)).ShouldBe(symbol);
    }

    [Fact]
    public async Task AddOrReactivate_Inactive_ReturnsReactivated()
    {
        var repository = Repository();
        var original = (await repository.FindAsync(Name("ADAUSDT"), Ct)).ShouldNotBeNull();
        (await repository.DeactivateAsync(Name("ADAUSDT"), Ct)).ShouldBeTrue();
        _clock.Advance(TimeSpan.FromHours(1));

        var outcome = await repository.AddOrReactivateAsync(Name("ADAUSDT"), Limit, Ct);

        outcome.Status.ShouldBe(SymbolAddStatus.Reactivated);
        var symbol = outcome.Symbol.ShouldNotBeNull();
        symbol.Id.ShouldBe(original.Id);
        symbol.IsActive.ShouldBeTrue();

        // The last deactivation is kept: the backfill uses it to find the inactive stretch to fill.
        symbol.DeactivatedAtUtc.ShouldBe(Now.UtcDateTime);
        symbol.CreatedAtUtc.ShouldBe(original.CreatedAtUtc);

        // So is the reactivation: the stretch ends at the first minute streamed since then.
        original.ReactivatedAtUtc.ShouldBeNull();
        symbol.ReactivatedAtUtc.ShouldBe(Now.UtcDateTime.AddHours(1));
        symbol.ReactivatedAtUtc!.Value.Kind.ShouldBe(DateTimeKind.Utc);
        (await repository.FindAsync(Name("ADAUSDT"), Ct)).ShouldBe(symbol);
    }

    [Fact]
    public async Task MarkPendingIfUnfinished_OnlyAnActiveSymbolThatHasNotCompleted()
    {
        var repository = Repository();
        var btc = (await repository.FindAsync(Name("BTCUSDT"), Ct)).ShouldNotBeNull();
        var ada = (await repository.FindAsync(Name("ADAUSDT"), Ct)).ShouldNotBeNull();
        var eth = (await repository.FindAsync(Name("ETHUSDT"), Ct)).ShouldNotBeNull();
        var from = new DateTime(2025, 11, 1, 0, 0, 0, DateTimeKind.Utc);
        await repository.SetBackfillStatusAsync(btc.Id, BackfillStatus.Failed, from, Ct);
        await repository.SetBackfillStatusAsync(ada.Id, BackfillStatus.Completed, from, Ct); // e.g. by another instance meanwhile
        (await repository.DeactivateAsync(Name("ETHUSDT"), Ct)).ShouldBeTrue(); // removed after the scan read it

        (await repository.MarkPendingIfUnfinishedAsync(btc.Id, Ct)).ShouldBeTrue();
        (await repository.MarkPendingIfUnfinishedAsync(ada.Id, Ct)).ShouldBeFalse();
        (await repository.MarkPendingIfUnfinishedAsync(eth.Id, Ct)).ShouldBeFalse();

        var stored = (await repository.ListAsync(Ct)).ToDictionary(s => s.Id);
        (stored[btc.Id].BackfillStatus, stored[btc.Id].BackfilledFromUtc).ShouldBe((BackfillStatus.Pending, (DateTime?)from)); // kept
        stored[ada.Id].BackfillStatus.ShouldBe(BackfillStatus.Completed);
        stored[eth.Id].BackfillStatus.ShouldBe(BackfillStatus.NotRequested);
    }

    [Fact]
    public async Task AddOrReactivate_Active_ReturnsAlreadyActive()
    {
        var repository = Repository();
        var original = (await repository.FindAsync(Name("BTCUSDT"), Ct)).ShouldNotBeNull();

        var outcome = await repository.AddOrReactivateAsync(Name("BTCUSDT"), Limit, Ct);

        outcome.ShouldBe(new SymbolAddOutcome(original, SymbolAddStatus.AlreadyActive));
    }

    [Fact]
    public async Task AddOrReactivate_Active_ReturnsAlreadyActiveEvenAtLimit()
    {
        var outcome = await Repository().AddOrReactivateAsync(Name("BTCUSDT"), 3, Ct);

        outcome.Status.ShouldBe(SymbolAddStatus.AlreadyActive);
        outcome.Symbol.ShouldNotBeNull().Name.ShouldBe("BTCUSDT");
    }

    [Fact]
    public async Task AddOrReactivate_AtLimit_ReturnsLimitReached()
    {
        var repository = Repository();
        await repository.DeactivateAsync(Name("ETHUSDT"), Ct);
        (await repository.AddOrReactivateAsync(Name("SOLUSDT"), 3, Ct)).Status.ShouldBe(SymbolAddStatus.Created);

        var created = await repository.AddOrReactivateAsync(Name("XRPUSDT"), 3, Ct);
        var reactivated = await repository.AddOrReactivateAsync(Name("ETHUSDT"), 3, Ct);

        created.ShouldBe(new SymbolAddOutcome(null, SymbolAddStatus.LimitReached));
        reactivated.ShouldBe(new SymbolAddOutcome(null, SymbolAddStatus.LimitReached));
        (await repository.FindAsync(Name("XRPUSDT"), Ct)).ShouldBeNull();
        (await repository.FindAsync(Name("ETHUSDT"), Ct)).ShouldNotBeNull().IsActive.ShouldBeFalse();
        (await repository.CountActiveAsync(Ct)).ShouldBe(3);
    }

    [Fact]
    public async Task AddOrReactivate_Concurrent_NeverExceedsLimit()
    {
        string[] names = ["AAAUSDT", "BBBUSDT", "CCCUSDT", "DDDUSDT", "EEEUSDT", "FFFUSDT", "GGGUSDT", "HHHUSDT"];

        var outcomes = await Task.WhenAll(names.Select(n =>
            Task.Run(() => Repository().AddOrReactivateAsync(Name(n), 6, Ct), Ct)));

        outcomes.Count(o => o.Status == SymbolAddStatus.Created).ShouldBe(3);
        outcomes.Count(o => o.Status == SymbolAddStatus.LimitReached).ShouldBe(5);
        (await Repository().CountActiveAsync(Ct)).ShouldBe(6);
    }

    [Fact]
    public async Task AddOrReactivate_NegativeLimit_Throws()
    {
        await Should.ThrowAsync<ArgumentOutOfRangeException>(
            () => Repository().AddOrReactivateAsync(Name("SOLUSDT"), -1, Ct));
    }

    [Fact]
    public async Task Deactivate_KeepsRow()
    {
        var repository = Repository();

        (await repository.DeactivateAsync(Name("ETHUSDT"), Ct)).ShouldBeTrue();

        var symbol = (await repository.FindAsync(Name("ETHUSDT"), Ct)).ShouldNotBeNull();
        symbol.IsActive.ShouldBeFalse();
        symbol.DeactivatedAtUtc.ShouldBe(Now.UtcDateTime);
        (await repository.ListAsync(Ct)).Select(s => s.Name).ShouldBe(["BTCUSDT", "ADAUSDT", "ETHUSDT"]);
        (await repository.ListActiveAsync(Ct)).Select(s => s.Name).ShouldBe(["BTCUSDT", "ADAUSDT"]);
        (await repository.CountActiveAsync(Ct)).ShouldBe(2);
    }

    [Fact]
    public async Task Deactivate_AlreadyInactive_KeepsFirstDeactivationTime()
    {
        var repository = Repository();
        await repository.DeactivateAsync(Name("ETHUSDT"), Ct);
        _clock.Advance(TimeSpan.FromMinutes(5));

        (await repository.DeactivateAsync(Name("ETHUSDT"), Ct)).ShouldBeTrue();

        (await repository.FindAsync(Name("ETHUSDT"), Ct)).ShouldNotBeNull().DeactivatedAtUtc.ShouldBe(Now.UtcDateTime);
    }

    [Fact]
    public async Task Deactivate_Unknown_ReturnsFalse()
    {
        (await Repository().DeactivateAsync(Name("NOPEUSDT"), Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task Find_Unknown_ReturnsNull()
    {
        (await Repository().FindAsync(Name("NOPEUSDT"), Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task SetBackfillStatus_StoresStatusAndFrom()
    {
        var repository = Repository();
        var btc = (await repository.FindAsync(Name("BTCUSDT"), Ct)).ShouldNotBeNull();
        var from = new DateTime(2025, 11, 1, 0, 0, 0, DateTimeKind.Utc);

        await repository.SetBackfillStatusAsync(btc.Id, BackfillStatus.Completed, from, Ct);

        var updated = (await repository.FindAsync(Name("BTCUSDT"), Ct)).ShouldNotBeNull();
        updated.BackfillStatus.ShouldBe(BackfillStatus.Completed);
        updated.BackfilledFromUtc.ShouldBe(from);

        await repository.SetBackfillStatusAsync(btc.Id, BackfillStatus.Running, null, Ct);

        updated = (await repository.FindAsync(Name("BTCUSDT"), Ct)).ShouldNotBeNull();
        updated.BackfillStatus.ShouldBe(BackfillStatus.Running);
        updated.BackfilledFromUtc.ShouldBeNull();
    }

    private static SymbolName Name(string raw) =>
        SymbolName.TryCreate(raw, out var name) ? name : throw new ArgumentException($"Invalid symbol {raw}", nameof(raw));

    private SymbolRepository Repository() => new(_dataSource, _clock);
}
