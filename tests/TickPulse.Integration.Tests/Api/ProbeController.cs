using System.Diagnostics.CodeAnalysis;
using System.Runtime.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using TickPulse.Api.Problems;
using TickPulse.Application.Results;

namespace TickPulse.Integration.Tests.Api;

/// <summary>A DTO shaped like the real contracts: <c>DataContract</c> XML, camelCase JSON, UTC timestamps.</summary>
[DataContract(Name = "probe", Namespace = "https://tickpulse.dev/schema/v1")]
public sealed class ProbeResponse
{
    [DataMember(Name = "symbol", Order = 1)]
    public string Symbol { get; init; } = string.Empty;

    [DataMember(Name = "price", Order = 2)]
    public decimal Price { get; init; }

    [DataMember(Name = "at", Order = 3)]
    public DateTime At { get; init; }
}

/// <summary>
/// Test-only endpoints, added to the host by <see cref="Fixtures.ApiFactory"/>, that exercise the host's negotiation,
/// problem and exception paths independently of the real analytics routes.
/// </summary>
[ApiController]
[Route("api/probe")]
public sealed class ProbeController : ControllerBase
{
    public const string KnownSymbol = "BTCUSDT";
    public const string SecretMessage = "probe-secret-detail";

    /// <summary>A <see cref="ProbeResponse"/> for <see cref="KnownSymbol"/>, otherwise a 404 <c>unknown-symbol</c> problem.</summary>
    [HttpGet("{symbol}")]
    public IActionResult Get(string symbol) =>
        string.Equals(symbol, KnownSymbol, StringComparison.OrdinalIgnoreCase)
            ? Ok(new ProbeResponse
            {
                Symbol = KnownSymbol,
                Price = 64_123.12345678m,
                At = new DateTime(2026, 3, 2, 12, 0, 0, DateTimeKind.Utc),
            })
            : new ProblemResult(ProblemFactory.Create(
                HttpContext,
                AppError.UnknownSymbol,
                StatusCodes.Status404NotFound,
                $"Symbol '{symbol}' is not monitored."));

    /// <summary>Always throws, to exercise the exception handler.</summary>
    [HttpGet("throw")]
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "MVC actions are instance methods.")]
    public IActionResult Throw() => throw new InvalidOperationException(SecretMessage);
}
