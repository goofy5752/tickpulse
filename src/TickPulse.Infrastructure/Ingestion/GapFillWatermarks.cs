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
/// <item>A connection that ended unexpectedly (and a session that ended) opens a gap for every known symbol and changes
/// every entry's version, so a fill that is still running (bounded by the connection that broke) cannot close it.</item>
/// <item><see cref="Lower"/> moves the last id back below trades known to be missing (a batch the writer dropped, the
/// start of a symbol's first minute), opens a gap and marks the entry as having a hole there.</item>
/// <item>Only a gap-fill that completed, and saw no change to the entry while it ran, closes the gap
/// (<see cref="Filled"/>); the version detects such changes. Closing the gap also clears the hole.</item>
/// <item>Only active symbols are tracked; <see cref="Forget"/> drops a removed one.</item>
/// <item><see cref="Seed"/>, at the start of a session, opens a gap after each symbol's last stored trade id, so the
/// trades that happened while this process did not lead (restart, redeploy, handover) are recovered too.</item>
/// </list>
/// <para>One entry holds one gap. An entry with a hole that also gets a later gap (a handover) fills from the hole, so
/// a long handover may take several connections (each fill is capped) to be recovered completely.</para>
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
                // Pending entries too: a fill running for one was bounded by the broken connection's first id, and
                // the trades after that connection's last id are missing as well.
                foreach (var entry in _entries.Values)
                {
                    entry.Pending = true;
                    entry.Version = ++_version;
                }
            }
        }
    }

    /// <summary>
    /// Opens a gap after the last trade id the database holds for each symbol, at the start of a leadership session:
    /// no stream reached this process while it did not lead (a restart, a redeploy, a handover), so gap-fill has to
    /// recover the trades after that id.
    /// </summary>
    /// <remarks>
    /// <para>An entry this process already has is raised to the stored id when that is higher: another instance may
    /// have led and stored trades since, and filling from the old id would only walk through them (up to the page cap)
    /// before the real gap. An entry with a hole (<see cref="Lower"/>) is kept as it is: its missing trades lie below
    /// the stored maximum.</para>
    /// <para>The highest stored id says nothing about holes below it. A hole that only the process which had it knew
    /// about (a dropped batch before a crash, a capped fill in another instance) is not seen here.</para>
    /// </remarks>
    /// <param name="lastStoredIds">Last stored trade id per symbol; the caller passes only active symbols.</param>
    public void Seed(IEnumerable<KeyValuePair<string, long>> lastStoredIds)
    {
        ArgumentNullException.ThrowIfNull(lastStoredIds);
        lock (_gate)
        {
            foreach (var (symbol, stored) in lastStoredIds)
            {
                if (!_entries.TryGetValue(symbol, out var entry))
                {
                    _entries[symbol] = new Entry { LastId = stored, Pending = true, Version = ++_version };
                }
                else if (!entry.Hole)
                {
                    entry.LastId = Math.Max(entry.LastId, stored);
                    entry.Pending = true;
                    entry.Version = ++_version;
                }
            }
        }
    }

    /// <summary>Whether the symbol has an entry, open gap or not.</summary>
    public bool Contains(string symbol)
    {
        lock (_gate)
        {
            return _entries.ContainsKey(symbol);
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
            entry.Hole = true;
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

    /// <summary>
    /// A gap-fill reached the stream of a connection that is still up, or the end of the data: the gap (and any hole) is
    /// closed, unless the entry changed meanwhile.
    /// </summary>
    public void Filled(string symbol, long version)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(symbol, out var entry) && entry.Version == version)
            {
                entry.Pending = false;
                entry.Hole = false;
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

        // Trades right after LastId are known to be missing (dropped batch, first-minute lead-in), so the entry must
        // not be raised to a later stored id.
        public bool Hole { get; set; }

        public long Version { get; set; }
    }
}
