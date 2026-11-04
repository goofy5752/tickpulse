using Microsoft.AspNetCore.Mvc;
using TickPulse.Api.Problems;
using TickPulse.Application.Results;

namespace TickPulse.Api.Controllers;

/// <summary>Turns a use-case result into a negotiated 200 or a TickPulse problem.</summary>
internal static class AppResultActions
{
    public static IActionResult ToActionResult<T, TContract>(this ControllerBase controller, AppResult<T> result, Func<T, TContract> map)
        where TContract : class =>
        result is { IsSuccess: true, Value: { } value }
            ? controller.Ok(map(value))
            : new ProblemResult(ProblemFactory.Create(controller.HttpContext, result.Error!.Value, result.Detail ?? string.Empty));

    /// <summary>A 400 <c>validation</c> problem naming one invalid parameter.</summary>
    public static ProblemResult InvalidParameter(this ControllerBase controller, string name, string message)
    {
        controller.ModelState.AddModelError(name, message);
        return new ProblemResult(ProblemFactory.CreateValidation(controller.HttpContext, controller.ModelState));
    }
}
