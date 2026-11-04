namespace TickPulse.Infrastructure.Persistence;

/// <summary>The leader election the ingestion worker owns; <see cref="PostgresLeaderLock"/> in production.</summary>
internal interface ILeaderLock : IAsyncDisposable
{
    /// <inheritdoc cref="PostgresLeaderLock.TryAcquireAsync"/>
    Task<bool> TryAcquireAsync(CancellationToken cancellationToken);

    /// <inheritdoc cref="PostgresLeaderLock.CheckAsync"/>
    Task<bool> CheckAsync(CancellationToken cancellationToken);

    /// <inheritdoc cref="PostgresLeaderLock.ReleaseAsync"/>
    Task ReleaseAsync();
}
