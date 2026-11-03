using TickPulse.Application.Results;

namespace TickPulse.Application.Tests;

public sealed class AppResultTests
{
    [Fact]
    public void Ok_IsSuccess()
    {
        var result = AppResult<int>.Ok(42);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(42);
        result.Error.ShouldBeNull();
        result.Detail.ShouldBeNull();
    }

    [Fact]
    public void Fail_CarriesErrorAndDetail()
    {
        var result = AppResult<string>.Fail(AppError.UnknownSymbol, "Symbol 'FOOBAR' is not monitored.");

        result.IsSuccess.ShouldBeFalse();
        result.Value.ShouldBeNull();
        result.Error.ShouldBe(AppError.UnknownSymbol);
        result.Detail.ShouldBe("Symbol 'FOOBAR' is not monitored.");
    }
}
