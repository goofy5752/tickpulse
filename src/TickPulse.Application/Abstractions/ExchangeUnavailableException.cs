namespace TickPulse.Application.Abstractions;

/// <summary>
/// The exchange could not answer right now (unreachable, failing, rate limiting or banning this IP), so the question
/// cannot be decided. Retrying later may succeed. Thrown by <see cref="IExchangeMetadata"/>.
/// </summary>
public sealed class ExchangeUnavailableException : Exception
{
    public ExchangeUnavailableException()
    {
    }

    public ExchangeUnavailableException(string message)
        : base(message)
    {
    }

    public ExchangeUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
