using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using TickPulse.Api.Problems;

namespace TickPulse.Api.Auth;

/// <summary>
/// The <c>ApiKey</c> scheme: a request is the admin when its single <c>X-Api-Key</c> header equals
/// <see cref="AdminOptions.ApiKey"/>, compared in constant time over the UTF-8 bytes. Fails closed: without a configured
/// key nobody is the admin, and the challenge is a 403 <c>admin-disabled</c> problem instead of a 401.
/// </summary>
/// <remarks>
/// A missing or wrong key is challenged with a 401 problem and <c>WWW-Authenticate: ApiKey</c>. Problems go through
/// <see cref="IProblemDetailsService"/>, so they are negotiated as JSON or XML like every other error. The key (sent or
/// configured) is never logged or echoed.
/// </remarks>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptionsMonitor<AdminOptions> admin) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";
    public const string HeaderName = "X-Api-Key";

    public const string MissingKeyDetail = "Send the admin API key in the X-Api-Key header.";
    public const string WrongKeyDetail = "The X-Api-Key header does not hold the admin API key.";
    public const string DisabledDetail = "Admin endpoints are disabled because no API key is configured.";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (ConfiguredKey() is not { } expected || !Request.Headers.TryGetValue(HeaderName, out var sent))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (sent.Count != 1 || !KeyEquals(sent[0], expected))
        {
            return Task.FromResult(AuthenticateResult.Fail(WrongKeyDetail));
        }

        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "admin")], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        if (ConfiguredKey() is null)
        {
            return WriteAsync(ProblemFactory.Create(Context, ProblemSlugs.AdminDisabled, StatusCodes.Status403Forbidden, DisabledDetail));
        }

        Response.Headers.WWWAuthenticate = SchemeName;
        var detail = Request.Headers.ContainsKey(HeaderName) ? WrongKeyDetail : MissingKeyDetail;

        // No TickPulse slug: an RFC 9457 about:blank problem titled "Unauthorized" (see ProblemDefaults).
        return WriteAsync(new ProblemDetails { Status = StatusCodes.Status401Unauthorized, Detail = detail });
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        WriteAsync(new ProblemDetails
        {
            Status = StatusCodes.Status403Forbidden,
            Detail = "The admin API key does not allow this request.",
        });

    /// <summary>Constant-time comparison of the UTF-8 bytes (only the lengths can differ in time).</summary>
    internal static bool KeyEquals(string? sent, string expected) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(sent ?? string.Empty), Encoding.UTF8.GetBytes(expected));

    private string? ConfiguredKey() => admin.CurrentValue.ApiKey is { } key && !string.IsNullOrWhiteSpace(key) ? key : null;

    private Task WriteAsync(ProblemDetails problem)
    {
        Response.StatusCode = problem.Status ?? StatusCodes.Status401Unauthorized;
        return Context.RequestServices.GetRequiredService<IProblemDetailsService>().WriteAsync(new ProblemDetailsContext
        {
            HttpContext = Context,
            ProblemDetails = problem,
        }).AsTask();
    }
}
