using System.Diagnostics.CodeAnalysis;

namespace TickPulse.Application.Results;

/// <summary>
/// The outcome of a use case: a value on success, or an <see cref="AppError"/> with a human-readable detail. A
/// <see cref="AppError.Validation"/> failure also names the offending request <see cref="Parameter"/>.
/// </summary>
public sealed record AppResult<T>(T? Value, AppError? Error, string? Detail)
{
    public bool IsSuccess => Error is null;

    /// <summary>The request parameter a validation failure is about, or <see langword="null"/>.</summary>
    public string? Parameter { get; init; }

    [SuppressMessage("Design", "CA1000:Do not declare static members on generic types", Justification = "AppResult<T>.Ok(value) is the intended factory shape; T cannot be inferred from a non-generic helper for Fail.")]
    public static AppResult<T> Ok(T value) => new(value, null, null);

    [SuppressMessage("Design", "CA1000:Do not declare static members on generic types", Justification = "AppResult<T>.Fail(error, detail) is the intended factory shape; T cannot be inferred from the arguments.")]
    public static AppResult<T> Fail(AppError error, string detail) => new(default, error, detail);

    /// <summary>A <see cref="AppError.Validation"/> failure of the request parameter <paramref name="parameter"/>.</summary>
    [SuppressMessage("Design", "CA1000:Do not declare static members on generic types", Justification = "AppResult<T>.Invalid(parameter, detail) is the intended factory shape; T cannot be inferred from the arguments.")]
    public static AppResult<T> Invalid(string parameter, string detail) =>
        new(default, AppError.Validation, detail) { Parameter = parameter };
}
