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
    /// <summary>Every symbol, active or not, with its backfill state.</summary>
    [HttpGet]
    [ProducesResponseType<AdminSymbolListResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ListAsync(CancellationToken ct) =>
        this.ToActionResult(await admin.ListAsync(ct), r => r.ToAdminContract());

    /// <summary>
    /// Starts monitoring a symbol that Binance spot trades: 201 when it was added or reactivated, 200 when it was
    /// already active. With backfill on, its history is fetched in the background.
    /// </summary>
    [HttpPost]
    [ProducesResponseType<AdminSymbolResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<AdminSymbolResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemResponse>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<ProblemResponse>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> AddAsync([FromBody] AddSymbolRequest request, CancellationToken ct) =>
        this.ToActionResult(
            await admin.AddAsync(request.Symbol, ct),
            r => r.Symbol.ToAdminContract(),
            r => r.Created ? StatusCodes.Status201Created : StatusCodes.Status200OK);

    /// <summary>Stops monitoring an active symbol. Its history is kept and stays queryable.</summary>
    [HttpDelete("{symbol}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemResponse>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RemoveAsync(string symbol, CancellationToken ct) =>
        this.ToNoContentResult(await admin.RemoveAsync(symbol, ct));
}
