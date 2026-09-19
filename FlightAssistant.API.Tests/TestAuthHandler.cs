using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using System.Text.Encodings.Web;

namespace FlightAssistant.API.Tests;

public class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "Test";

    public TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder) { }

    // Accepts "Bearer test-{guid}" for users, "Bearer test-admin-{guid}" for admins, and
    // "Bearer test-noscope-{guid}" for an authenticated caller whose token carries no API scope
    // (#413). Without that third form no test could produce a scope failure at all, so the defect
    // it causes — a bodyless 403 whose escaping exception was logged as a 500 — was unreachable
    // from this suite.
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var authHeader = Request.Headers.Authorization.ToString();
        if (!authHeader.StartsWith("Bearer test-"))
            return Task.FromResult(AuthenticateResult.NoResult());

        var token = authHeader["Bearer test-".Length..];
        var isAdmin = token.StartsWith("admin-");
        var hasNoScope = token.StartsWith("noscope-");
        var userId = isAdmin
            ? token["admin-".Length..]
            : hasNoScope ? token["noscope-".Length..] : token;

        var claims = new List<Claim> { new("oid", userId) };

        if (!hasNoScope)
        {
            // Both claim forms, because both occur in the real tenant and ClaimsPrincipalExtensions
            // .HasScope accepts either.
            claims.Add(new Claim("scp", "access_as_user"));
            claims.Add(new Claim("http://schemas.microsoft.com/identity/claims/scope", "access_as_user"));
        }

        if (isAdmin)
            claims.Add(new Claim("roles", "admin"));

        var identity = new ClaimsIdentity(claims, SchemeName, nameType: "name", roleType: "roles");
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
