namespace TickPulse.Application.Results;

/// <summary>The ways a use case can fail; the API maps each one to a problem type.</summary>
public enum AppError
{
    UnknownSymbol,
    NoData,
    InvalidSymbol,
    SymbolLimit,
    Validation,

    /// <summary>A dependency outside TickPulse (the exchange) cannot answer right now; retrying later may help.</summary>
    UpstreamUnavailable,
}
