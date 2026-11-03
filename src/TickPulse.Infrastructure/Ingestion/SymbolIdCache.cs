using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using TickPulse.Application.Abstractions;

namespace TickPulse.Infrastructure.Ingestion;

/// <summary>
/// In-memory map between symbol names and database ids, so the trade pump never touches the database. Refreshed by
/// <see cref="SymbolReconciler"/> on every pass, before anything is subscribed.
/// </summary>
/// <remarks>
/// Entries are never removed: ids and names are stable, and a deactivated symbol can still have ticks in flight.
/// Thread-safe.
/// </remarks>
public sealed class SymbolIdCache
{
    private readonly ConcurrentDictionary<string, int> _ids = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, string> _names = new();

    public void Update(IEnumerable<MonitoredSymbol> symbols)
    {
        ArgumentNullException.ThrowIfNull(symbols);
        foreach (var symbol in symbols)
        {
            _ids[symbol.Name] = symbol.Id;
            _names[symbol.Id] = symbol.Name;
        }
    }

    public bool TryGetId(string name, out int id) => _ids.TryGetValue(name, out id);

    public bool TryGetName(int id, [NotNullWhen(true)] out string? name) => _names.TryGetValue(id, out name);
}
