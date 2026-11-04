using TickPulse.Application.Abstractions;

namespace TickPulse.Api.Hosting;

/// <summary>The database-backed figures of <see cref="Contracts.SystemStatsResponse"/>.</summary>
public sealed record StoredCounts(long StoredTicksApprox, int ActiveSymbols);

/// <summary>Reads <see cref="StoredCounts"/>; the one part of the statistics that needs the database.</summary>
public interface IStoredCounts
{
    Task<StoredCounts> GetAsync(CancellationToken ct);
}

/// <summary><see cref="IStoredCounts"/> over the approximate tick count and the number of active symbols.</summary>
internal sealed class DatabaseStoredCounts(IMarketDataQueries queries, ISymbolRepository symbols) : IStoredCounts
{
    public async Task<StoredCounts> GetAsync(CancellationToken ct)
    {
        var storedTicks = await queries.ApproximateTickCountAsync(ct).ConfigureAwait(false);
        var activeSymbols = await symbols.CountActiveAsync(ct).ConfigureAwait(false);
        return new StoredCounts(storedTicks, activeSymbols);
    }
}
