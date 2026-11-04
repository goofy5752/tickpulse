using Microsoft.AspNetCore.Mvc;
using TickPulse.Api.Problems;
using TickPulse.Application.Results;

namespace TickPulse.Api.Controllers;

/// <summary>Turns a use-case result into a negotiated 200 or a TickPulse problem.</summary>
internal static class AppResultActions
{
    /// <summary>
    /// A 200 with the mapped contract, or the problem of the failure. A validation failure that names its parameter
    /// becomes a validation problem with that parameter's message under <c>errors</c>.
    /// </summary>
    public static IActionResult ToActionResult<T, TContract>(this ControllerBase controller, AppResult<T> result, Func<T, TContract> map)
        where TContract : class
    {
        if (result is { IsSuccess: true, Value: { } value })
        {
            return controller.Ok(map(value));
        }

        var error = result.Error ?? throw new InvalidOperationException("A successful result carries no value.");
        var detail = result.Detail ?? string.Empty;
        return error == AppError.Validation && result.Parameter is { } parameter
            ? controller.InvalidParameter(parameter, detail)
            : new ProblemResult(ProblemFactory.Create(controller.HttpContext, error, detail));
    }

    /// <summary>A 400 <c>validation</c> problem about one parameter; <paramref name="message"/> is also the detail.</summary>
    public static ProblemResult InvalidParameter(this ControllerBase controller, string parameter, string message) =>
        new(ProblemFactory.CreateValidation(controller.HttpContext, parameter, message));
}
