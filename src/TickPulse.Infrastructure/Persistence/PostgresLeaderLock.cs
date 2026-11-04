using System.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace TickPulse.Infrastructure.Persistence;

/// <summary>
/// Leader election with a session-level PostgreSQL advisory lock held on a dedicated, unpooled connection. Only the
/// holder ingests and backfills. The lock lives exactly as long as that session: if the connection breaks (database
/// restart, failover, network cut, <c>pg_terminate_backend</c>) the server releases it, so another instance can take
/// over, and this one must stop at once. <see cref="CheckAsync"/> detects that.
/// </summary>
/// <remarks>
/// <para><b>Single owner.</b> Exactly one component owns an instance: the ingestion worker, which creates it itself (it is
/// not registered in the container). <see cref="ReleaseAsync"/> gives the lock up for the whole process, so a second
/// user would silently lose it; other leader-only components read <c>IngestionState.IsLeader</c> instead.</para>
/// <para>Thread-safe: every operation on the connection is serialised.</para>
/// </remarks>
public sealed partial class PostgresLeaderLock : ILeaderLock
{
    /// <summary>The advisory lock key, <c>"TICK"</c> in ASCII.</summary>
    public const long LockKey = 0x5449434B;

    // Short command timeout: the liveness probe must notice a hung connection quickly.
    private const int CommandTimeoutSeconds = 5;

    private readonly string _connectionString;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private NpgsqlConnection? _connection;
    private int _disposed;

    /// <param name="connectionString">The TickPulse database; pooling is turned off for the lock's connection.</param>
    /// <param name="logger">Logger for acquisitions and losses.</param>
    /// <param name="allowGssEncryption">
    /// <c>Database:AllowGssEncryption</c>: GSS encryption stays off unless it is true (see <see cref="NpgsqlConnectionPolicy"/>).
    /// </param>
    public PostgresLeaderLock(string connectionString, ILogger<PostgresLeaderLock>? logger = null, bool allowGssEncryption = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        // Unpooled: closing the connection must end the session, which is what releases the lock.
        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Pooling = false,
            TcpKeepAlive = true,
            CommandTimeout = CommandTimeoutSeconds,
            ApplicationName = "tickpulse-leader",
        };
        NpgsqlConnectionPolicy.Apply(builder, allowGssEncryption);
        _connectionString = builder.ConnectionString;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>The effective settings of the lock's connection.</summary>
    internal string ConnectionString => _connectionString;

    /// <summary>Whether the lock is held:it was acquired and its connection has not broken or been closed since.</summary>
    /// <remarks>A silently dead connection is only discovered by the next <see cref="CheckAsync"/>.</remarks>
    public bool IsHeld => Volatile.Read(ref _connection) is { FullState: ConnectionState.Open };

    /// <summary>
    /// Takes the lock without waiting (<c>pg_try_advisory_lock</c>). Returns <see langword="true"/> when this instance
    /// holds it (also when it already did), <see langword="false"/> when another session holds it. Nothing stays open
    /// when it returns <see langword="false"/>.
    /// </summary>
    /// <exception cref="NpgsqlException">The database could not be reached.</exception>
    public async Task<bool> TryAcquireAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsHeld)
            {
                return true;
            }

            await CloseCoreAsync().ConfigureAwait(false);
            var connection = new NpgsqlConnection(_connectionString);
            var keep = false;
            try
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock($1)", connection);
                command.Parameters.Add(new NpgsqlParameter<long> { TypedValue = LockKey });
                keep = (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
                if (keep)
                {
                    Volatile.Write(ref _connection, connection);
                    LogAcquired(connection.ProcessID);
                }

                return keep;
            }
            finally
            {
                if (!keep)
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Probes the lock's connection with a round trip. Returns <see langword="false"/>, and closes the connection,
    /// when the lock is no longer held.
    /// </summary>
    public async Task<bool> CheckAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var connection = Volatile.Read(ref _connection);
            if (connection is null)
            {
                return false;
            }

            if (connection.FullState == ConnectionState.Open)
            {
                try
                {
                    await using var command = new NpgsqlCommand("SELECT 1", connection);
                    await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                    return true;
                }
                catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or TimeoutException)
                {
                    LogLost(ex);
                }
            }
            else
            {
                LogLost(null);
            }

            await CloseCoreAsync().ConfigureAwait(false);
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Gives the lock up by closing its session. The instance can acquire again afterwards.</summary>
    public async Task ReleaseAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _connection) is { } connection)
            {
                LogReleased(connection.ProcessID);
            }

            await CloseCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await ReleaseAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    // Caller holds _gate.
    private async Task CloseCoreAsync()
    {
        var connection = Interlocked.Exchange(ref _connection, null);
        if (connection is not null)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Leader lock acquired (backend {Pid})")]
    private partial void LogAcquired(int pid);

    [LoggerMessage(Level = LogLevel.Information, Message = "Leader lock released (backend {Pid})")]
    private partial void LogReleased(int pid);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Leader lock connection lost; leadership is gone")]
    private partial void LogLost(Exception? exception);
}
