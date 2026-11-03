namespace TickPulse.Application.Abstractions;

/// <summary>In-process signal that the set of active symbols changed.</summary>
public interface ISymbolChangeSignal
{
    void Notify();

    ValueTask WaitAsync(CancellationToken ct);
}
