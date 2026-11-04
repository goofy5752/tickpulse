using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using TickPulse.Api.Hosting;
using TickPulse.Api.Problems;
using TickPulse.Api.Telemetry;

namespace TickPulse.Api.Auth;

/// <summary>
/// The <c>ApiKey</c> scheme: a request is the admin when its single <c>X-Api-Key</c> header matches the configured
/// <see cref="AdminApiKey"/>, compared in constant time. Only the header counts, never the query string or a cookie.
/// Fails closed: without a configured key nobody is the admin, and the challenge is a 403 <c>admin-disabled</c>
/// problem instead of a 401.
/// </summary>
/// <remarks>
/// A missing or wrong key is challenged with a 401 <c>unauthorized</c> problem and <c>WWW-Authenticate: ApiKey</c>,
/// and logged as a warning with the hashed client address. Problems go through <see cref="IProblemDetailsService"/>,
/// so they are negotiated as JSON or XML like every other error. The key (sent or configured) is never logged or
/// echoed.
/// </remarks>
public sealed partial class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    AdminApiKey key) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";
    public const string HeaderName = "X-Api-Key";

    public const string MissingKeyDetail = "Send the admin API key in the X-Api-Key header.";
    public const string WrongKeyDetail = "The X-Api-Key header does not hold the admin API key.";
    public const string DisabledDetail = "Admin endpoints are disabled because no API key is configured.";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!key.IsConfigured || !Request.Headers.TryGetValue(HeaderName, out var sent))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (sent.Count != 1 || !key.Matches(sent[0]))
        {
            return Task.FromResult(AuthenticateResult.Fail(WrongKeyDetail));
        }

        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "admin")], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        if (!key.IsConfigured)
        {
            return WriteAsync(ProblemFactory.Create(Context, ProblemSlugs.AdminDisabled, StatusCodes.Status403Forbidden, DisabledDetail));
        }

        var presented = Request.Headers.ContainsKey(HeaderName);
        var hasher = Context.RequestServices.GetRequiredService<ClientIpHasher>();
        LogAuthenticationFailed(
            Logger,
            presented ? "wrong key" : "missing key",
            hasher.Hash(ClientAddressing.ClientAddress(Context)),
            Request.Method,
            Request.Path);

        Response.Headers.WWWAuthenticate = SchemeName;
        return WriteAsync(ProblemFactory.Create(
            Context,
            ProblemSlugs.Unauthorized,
            StatusCodes.Status401Unauthorized,
            presented ? WrongKeyDetail : MissingKeyDetail));
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        WriteAsync(new ProblemDetails
        {
            Status = StatusCodes.Status403Forbidden,
            Detail = "The admin API key does not allow this request.",
        });

    private Task WriteAsync(ProblemDetails problem)
    {
        Response.StatusCode = problem.Status ?? StatusCodes.Status401Unauthorized;
        return Context.RequestServices.GetRequiredService<IProblemDetailsService>().WriteAsync(new ProblemDetailsContext
        {
            HttpContext = Context,
            ProblemDetails = problem,
        }).AsTask();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Admin authentication failed ({Reason}) for client {ClientIpHash} on {Method} {Path}")]
    private static partial void LogAuthenticationFailed(ILogger logger, string reason, string clientIpHash, string method, PathString path);
}
