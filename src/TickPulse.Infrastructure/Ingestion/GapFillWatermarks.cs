namespace TickPulse.Infrastructure.Ingestion;

/// <summary>Lets the batch writer report trades it could not store, so that gap-fill fetches them again.</summary>
public interface IGapFillWatermark
{
    /// <summary>Trades of <paramref name="symbol"/> from <paramref name="firstMissingId"/> on may be missing.</summary>
    void Lower(string symbol, long firstMissingId);
}

/// <summary>
/// Per symbol, the last trade id known to be stored before a gap, and whether a gap after it is still open. Gap-fill
/// starts from <c>LastId + 1</c>.
/// </summary>
/// <remarks>
/// <para>Rules:</para>
/// <list type="bullet">
/// <item><see cref="Merge"/> raises a symbol's last id to what an ended connection delivered, but never while a gap is
/// open: a newer id would skip the trades in the gap.</item>
/// <item>A connection that ended unexpectedly (and a session that ended) opens a gap for every known symbol.</item>
/// <item><see cref="Lower"/> moves the last id back below trades the writer dropped, and opens a gap.</item>
/// <item>Only a gap-fill that completed, and saw no change to the entry while it ran, closes the gap
/// (<see cref="Filled"/>); the version detects such changes.</item>
/// <item>Only active symbols are tracked; <see cref="Forget"/> drops a removed one.</item>
/// </list>
/// <para>Lives for the whole process, so gaps survive leadership sessions. Thread-safe.</para>
/// </remarks>
public sealed class GapFillWatermarks(SymbolIdCache symbols) : IGapFillWatermark
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private long _version;

    /// <summary>
    /// Folds in the last ids a finished connection delivered. With <paramref name="gap"/> the connection ended
    /// unexpectedly (or the session did), so every known symbol now has an open gap.
    /// </summary>
    public void Merge(IEnumerable<KeyValuePair<string, long>> lastIds, bool gap)
    {
        ArgumentNullException.ThrowIfNull(lastIds);
        lock (_gate)
        {
            foreach (var (symbol, lastId) in lastIds)
            {
                if (!symbols.IsActive(symbol))
                {
                    continue; // removed meanwhile; a later re-add starts fresh
                }

                if (!_entries.TryGetValue(symbol, out var entry))
                {
                    _entries[symbol] = new Entry { LastId = lastId, Version = ++_version };
                }
                else if (!entry.Pending && lastId > entry.LastId)
                {
                    entry.LastId = lastId;
                    entry.Version = ++_version;
                }
            }

            if (gap)
            {
                foreach (var entry in _entries.Values.Where(e => !e.Pending))
                {
                    entry.Pending = true;
                    entry.Version = ++_version;
                }
            }
        }
    }

    /// <inheritdoc />
    public void Lower(string symbol, long firstMissingId)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        if (!symbols.IsActive(symbol))
        {
            return;
        }

        lock (_gate)
        {
            var below = firstMissingId - 1;
            if (_entries.TryGetValue(symbol, out var entry))
            {
                entry.LastId = Math.Min(entry.LastId, below);
            }
            else
            {
                entry = _entries[symbol] = new Entry { LastId = below };
            }

            entry.Pending = true;
            entry.Version = ++_version;
        }
    }

    /// <summary>Where a gap-fill for the symbol starts, and the entry version to report back.</summary>
    /// <param name="symbol">The symbol.</param>
    /// <param name="pendingOnly">Only when a gap is open.</param>
    /// <param name="lastId">The last trade id stored before the gap.</param>
    /// <param name="version">Pass to <see cref="Filled"/> or <see cref="Advance"/>.</param>
    public bool TryGetStart(string symbol, bool pendingOnly, out long lastId, out long version)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(symbol, out var entry) && (entry.Pending || !pendingOnly))
            {
                lastId = entry.LastId;
                version = entry.Version;
                return true;
            }
        }

        lastId = 0;
        version = 0;
        return false;
    }

    /// <summary>A gap-fill reached the stream or the end of the data: the gap is closed, unless it changed meanwhile.</summary>
    public void Filled(string symbol, long version)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(symbol, out var entry) && entry.Version == version)
            {
                entry.Pending = false;
            }
        }
    }

    /// <summary>A gap-fill stopped early after recovering up to <paramref name="lastId"/>: the gap stays open from there.</summary>
    public void Advance(string symbol, long lastId, long version)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(symbol, out var entry) && entry.Version == version && lastId > entry.LastId)
            {
                entry.LastId = lastId;
                entry.Version = ++_version;
            }
        }
    }

    /// <summary>The symbol is no longer monitored.</summary>
    public void Forget(string symbol)
    {
        lock (_gate)
        {
            _entries.Remove(symbol);
        }
    }

    private sealed class Entry
    {
        public long LastId { get; set; }

        public bool Pending { get; set; }

        public long Version { get; set; }
    }
}
