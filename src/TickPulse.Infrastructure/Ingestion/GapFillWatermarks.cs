namespace TickPulse.Infrastructure.Ingestion;

/// <summary>Lets the batch writer report trades it could not store, so that gap-fill fetches them again.</summary>
public interface IGapFillWatermark
{
    /// <summary>Trades of <paramref name="symbol"/> from <paramref name="firstMissingId"/> on may be missing.</summary>
    void Lower(string symbol, long firstMissingId);
}

/// <summary>The first and last trade id one connection delivered for a symbol. Binance streams are contiguous within a connection, so every id between was delivered too.</summary>
public readonly record struct IdSpan(long First, long Last);

/// <summary>
/// Per symbol, the end of the contiguous prefix of trade ids this process has accounted for (<c>LastId</c>), and whether
/// a gap after it is still open. Gap-fill starts from <c>LastId + 1</c>.
/// </summary>
/// <remarks>
/// <para>Safety: no trade id is ever skipped; recovering a trade twice is harmless (inserts are idempotent). Every id up to
/// <c>LastId</c> is stored, on its way to the writer, or was stored by another instance while this one did not lead
/// (<see cref="Seed"/>). An id that the writer could not store is reported through <see cref="Lower"/>, which moves
/// <c>LastId</c> back below it. So the one rule every operation keeps is: <b><c>LastId</c> only moves up across ids that
/// were delivered or stored by another instance.</b></para>
/// <list type="bullet">
/// <item><see cref="Merge"/> raises a closed entry across the span a finished connection delivered, but only when that
/// span is contiguous with the prefix (<c>First &lt;= LastId + 1</c>). A span that starts higher means the connection it
/// replaced stalled before it died: the ids between are missing and a gap is opened. A pending entry is never raised
/// (that would skip its gap).</item>
/// <item><see cref="Verify"/> applies the same check to the connection that is still up, on every reconcile pass, so such
/// a gap is noticed within a pass instead of at that connection's end.</item>
/// <item>A gap closes only through <see cref="Filled"/>: a fill that emitted every id from <c>LastId + 1</c> up to the
/// live stream (or the end of the data) and saw no change meanwhile (the version). It raises <c>LastId</c> to the last
/// emitted id. A fill that stopped early (<see cref="Advance"/>) raises it the same way and leaves the gap open.</item>
/// <item>The highest id any ended connection delivered, or that <see cref="Lower"/> reported missing, is kept per symbol
/// (<c>Above</c>, never lowered, dropped only by <see cref="Forget"/>). <see cref="Seed"/> raises an entry to the stored
/// id only when <c>Above &lt;= LastId</c>: then nothing this process delivered lies above the prefix, so whatever is
/// stored above was stored by another instance that led meanwhile. Otherwise the fill walks up from <c>LastId + 1</c>
/// through what is stored (one page per thousand ids, repeated every reconcile pass) until it reaches the stream.</item>
/// <item>Only active symbols are tracked; <see cref="Forget"/> drops a removed one entirely.</item>
/// </list>
/// <para>One entry holds one gap. An entry that cannot be raised and also gets a later gap (a handover) fills from the
/// earlier one; the supervisor keeps filling an open gap on every reconcile pass, so it catches up.</para>
/// <para>Lives for the whole process, so gaps survive leadership sessions. Thread-safe.</para>
/// </remarks>
public sealed class GapFillWatermarks(SymbolIdCache symbols) : IGapFillWatermark
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    // Kept apart from the entries: a discarded link's ids may arrive before the entry exists, in any order with Merge.
    private readonly Dictionary<string, long> _above = new(StringComparer.OrdinalIgnoreCase);
    private long _version;

    /// <summary>
    /// Folds in the spans a finished connection delivered. With <paramref name="gap"/> the connection ended unexpectedly
    /// (or the session did), so every known symbol now has an open gap.
    /// </summary>
    public void Merge(IEnumerable<KeyValuePair<string, IdSpan>> spans, bool gap)
    {
        ArgumentNullException.ThrowIfNull(spans);
        lock (_gate)
        {
            foreach (var (symbol, span) in spans)
            {
                if (!symbols.IsActive(symbol))
                {
                    continue; // removed meanwhile; a later re-add starts fresh
                }

                RecordAbove(symbol, span.Last);
                if (!_entries.TryGetValue(symbol, out var entry))
                {
                    _entries[symbol] = new Entry { LastId = span.Last, Version = ++_version };
                }
                else if (!entry.Pending && span.First <= entry.LastId + 1)
                {
                    if (span.Last > entry.LastId)
                    {
                        entry.LastId = span.Last;
                        entry.Version = ++_version;
                    }
                }
                else if (!entry.Pending)
                {
                    Open(entry); // the stream started above the prefix: the ids between are missing
                }
            }

            if (gap)
            {
                // Pending entries too: a fill running for one was bounded by the broken connection's first id, and
                // the trades after that connection's last id are missing as well.
                foreach (var entry in _entries.Values)
                {
                    Open(entry);
                }
            }
        }
    }

    /// <summary>
    /// Checks the spans a connection that is still up has delivered so far: a closed entry whose stream started above
    /// <c>LastId + 1</c> gets its gap opened now (the connection this one replaced stalled before it was retired, or a fill
    /// closed on REST data that lagged behind the stream). Never raises an entry and never creates one.
    /// </summary>
    public void Verify(IEnumerable<KeyValuePair<string, IdSpan>> spans)
    {
        ArgumentNullException.ThrowIfNull(spans);
        lock (_gate)
        {
            foreach (var (symbol, span) in spans)
            {
                if (_entries.TryGetValue(symbol, out var entry) && !entry.Pending && span.First > entry.LastId + 1)
                {
                    Open(entry);
                }
            }
        }
    }

    /// <summary>
    /// Records what a discarded rotation link delivered (so those ids are stored, or were reported dropped) without
    /// moving any gap-fill start: that link never became the stream this process follows, and the connection it failed
    /// to replace decides at its own end.
    /// </summary>
    public void NoteStored(IEnumerable<KeyValuePair<string, IdSpan>> spans)
    {
        ArgumentNullException.ThrowIfNull(spans);
        lock (_gate)
        {
            foreach (var (symbol, span) in spans)
            {
                if (symbols.IsActive(symbol))
                {
                    RecordAbove(symbol, span.Last);
                }
            }
        }
    }

    /// <summary>
    /// Opens a gap after the id of the newest trade the database holds for each symbol, at the start of a leadership
    /// session: no stream reached this process while it did not lead (a restart, a redeploy, a handover), so gap-fill
    /// has to recover the trades after that id.
    /// </summary>
    /// <remarks>
    /// <para>An entry this process already has is raised to the stored id when that is higher, but only if nothing this
    /// process delivered lies above its start: then whatever is stored above was stored by another instance that led
    /// since, and filling from the old id would only walk through it before the real gap. Otherwise the entry is kept
    /// where it is, whatever the stored id: it may well be this process's own trades above the gap.</para>
    /// <para>A symbol without an entry starts at the stored id. Nothing this process delivered for it can be missing:
    /// a dropped batch would have created the entry through <see cref="Lower"/>.</para>
    /// <para>The stored id (that of the newest stored trade) says nothing about holes below it. A hole that only the
    /// process which had it knew about (a dropped batch before a crash, a capped fill in another instance) is not seen
    /// here.</para>
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
                    entry = _entries[symbol] = new Entry { LastId = stored };
                }
                else if (_above.GetValueOrDefault(symbol) <= entry.LastId && stored > entry.LastId)
                {
                    entry.LastId = stored;
                }

                entry.Pending = true;
                entry.Version = ++_version;
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

    /// <summary>Whether the symbol has an open gap.</summary>
    public bool IsPending(string symbol)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(symbol, out var entry) && entry.Pending;
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

            // Known to exist above the start, though not stored: blocks a seed until a contiguous span passes it.
            RecordAbove(symbol, firstMissingId);
            Open(entry);
        }
    }

    /// <summary>Where a gap-fill for the symbol starts, and the entry version to report back.</summary>
    /// <param name="symbol">The symbol.</param>
    /// <param name="pendingOnly">Only when a gap is open.</param>
    /// <param name="lastId">The last trade id accounted for before the gap.</param>
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
    /// A gap-fill emitted every id from the start up to the live stream, or to the end of the data: the gap is closed and
    /// the prefix ends at <paramref name="lastEmittedId"/> (none emitted: unchanged), unless the entry changed meanwhile.
    /// </summary>
    public void Filled(string symbol, long version, long? lastEmittedId)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(symbol, out var entry) && entry.Version == version)
            {
                entry.LastId = Math.Max(entry.LastId, lastEmittedId ?? entry.LastId);
                entry.Pending = false;
                entry.Version = ++_version;
            }
        }
    }

    /// <summary>
    /// A gap-fill stopped early (the page cap, a failure after some pages) after emitting every id from the start up to
    /// <paramref name="lastEmittedId"/>: the gap stays open from there, unless the entry changed meanwhile.
    /// </summary>
    public void Advance(string symbol, long lastEmittedId, long version)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(symbol, out var entry) && entry.Pending && entry.Version == version && lastEmittedId > entry.LastId)
            {
                entry.LastId = lastEmittedId;
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
            _above.Remove(symbol);
        }
    }

    // Callers hold the gate.
    private void Open(Entry entry)
    {
        entry.Pending = true;
        entry.Version = ++_version;
    }

    private void RecordAbove(string symbol, long id)
    {
        if (id > _above.GetValueOrDefault(symbol))
        {
            _above[symbol] = id;
        }
    }

    private sealed class Entry
    {
        public long LastId { get; set; }

        public bool Pending { get; set; }

        public long Version { get; set; }
    }
}
