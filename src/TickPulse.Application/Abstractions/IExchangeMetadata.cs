using TickPulse.Domain;

namespace TickPulse.Application.Abstractions;

/// <summary>Exchange reference data.</summary>
public interface IExchangeMetadata
{
    Task<bool> IsTradingSymbolAsync(SymbolName name, CancellationToken ct);
}
