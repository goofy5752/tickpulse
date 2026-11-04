using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TickPulse.Api.Auth;
using TickPulse.Application.Symbols;
using TickPulse.Contracts;

namespace TickPulse.Api.Controllers;

/// <summary>
/// Admin management of the monitored symbols. Every route needs the admin API key in <c>X-Api-Key</c> (401 without
/// it, 403 <c>admin-disabled</c> when the server has none). Symbols are case-insensitive.
/// </summary>
[ApiController]
[Route("api/admin/symbols")]
[Authorize(Policy = AdminAccess.Policy)]
[ProducesResponseType<ProblemResponse>(StatusCodes.Status401Unauthorized)]
[ProducesResponseType<ProblemResponse>(StatusCodes.Status403Forbidden)]
public sealed class AdminSymbolsController(SymbolAdminService admin) : ControllerBase
{
    private const string GetRouteName = "GetAdminSymbol";

    /// <summary>Every symbol, active or not, with its backfill state.</summary>
    [HttpGet]
    [ProducesResponseType<AdminSymbolListResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ListAsync(CancellationToken ct) =>
        this.ToActionResult(await admin.ListAsync(ct), r => r.ToAdminContract());

    /// <summary>One symbol, active or not, with its backfill state; 404 <c>unknown-symbol</c> when it is not known.</summary>
    [HttpGet("{symbol}", Name = GetRouteName)]
    [ProducesResponseType<AdminSymbolResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemResponse>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetAsync(string symbol, CancellationToken ct) =>
        this.ToActionResult(await admin.GetAsync(symbol, ct), r => r.ToAdminContract());

    /// <summary>
    /// Starts monitoring a symbol that Binance spot trades: 201 with a <c>Location</c> of the symbol when it was added
    /// or reactivated, 200 when it was already active. A missing or blank <c>symbol</c> is a 400 <c>validation</c>
    /// problem, a malformed one a 422 <c>invalid-symbol</c>. With backfill on, its history is fetched in the background.
    /// </summary>
    [HttpPost]
    [ProducesResponseType<AdminSymbolResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<AdminSymbolResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemResponse>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<ProblemResponse>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> AddAsync([FromBody] AddSymbolRequest? request, CancellationToken ct)
    {
        // An empty body binds as null (the parameter is nullable), so it gets the same `symbol` validation problem as
        // a body without the field.
        var result = await admin.AddAsync(request?.Symbol, ct);
        return result is { IsSuccess: true, Value: { Created: true } added }
            ? Created(Url.RouteUrl(GetRouteName, new { symbol = added.Symbol.Name }), added.Symbol.ToAdminContract())
            : this.ToActionResult(result, r => r.Symbol.ToAdminContract());
    }

    /// <summary>Stops monitoring an active symbol. Its history is kept and stays queryable.</summary>
    [HttpDelete("{symbol}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemResponse>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RemoveAsync(string symbol, CancellationToken ct) =>
        this.ToNoContentResult(await admin.RemoveAsync(symbol, ct));
}
