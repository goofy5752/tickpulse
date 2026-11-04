using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using TickPulse.Application.Abstractions;

namespace TickPulse.Infrastructure.Ingestion;

/// <summary>
/// In-memory map between symbol names and database ids, so the trade pump never touches the database, plus the set
/// of currently active symbols. Refreshed by <see cref="SymbolReconciler"/> on every pass, before anything is
/// subscribed.
/// </summary>
/// <remarks>
/// Id entries are never removed: ids and names are stable, and a deactivated symbol can still have ticks in flight.
/// Thread-safe.
/// </remarks>
public sealed class SymbolIdCache
{
    private readonly ConcurrentDictionary<string, int> _ids = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, string> _names = new();
    private volatile FrozenSet<string> _active = FrozenSet<string>.Empty;

    /// <summary>Records the complete set of active symbols, as read from the database.</summary>
    public void Update(IEnumerable<MonitoredSymbol> activeSymbols)
    {
        ArgumentNullException.ThrowIfNull(activeSymbols);
        var active = new List<string>();
        foreach (var symbol in activeSymbols)
        {
            _ids[symbol.Name] = symbol.Id;
            _names[symbol.Id] = symbol.Name;
            if (symbol.IsActive)
            {
                active.Add(symbol.Name);
            }
        }

        _active = active.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }

    public bool TryGetId(string name, out int id) => _ids.TryGetValue(name, out id);

    public bool TryGetName(int id, [NotNullWhen(true)] out string? name) => _names.TryGetValue(id, out name);

    /// <summary>Whether the symbol was active at the last refresh.</summary>
    public bool IsActive(string name) => _active.Contains(name);
}
