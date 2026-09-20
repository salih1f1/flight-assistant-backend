using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using System.Text.Encodings.Web;

namespace FlightAssistant.API.Tests;

public class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "Test";

    /// <summary>The <c>name</c> claim every test token carries unless a test overrides it.</summary>
    public const string DisplayName = "Test User";

    /// <summary>
    /// Headers a test uses to put a specific identity in the token, so a test about what the
    /// server reads from a token does not have to encode it into the bearer string (#544).
    /// </summary>
    public const string EmailHeader = "X-Test-Email";
    public const string DisplayNameHeader = "X-Test-Display-Name";

    public TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder) { }

    // Accepts "Bearer test-{guid}" for users, "Bearer test-admin-{guid}" for admins,
    // "Bearer test-noscope-{guid}" for an authenticated caller whose token carries no API scope
    // (#413), and "Bearer test-noemail-{guid}" for one carrying no email claim of any kind (#544).
    // Without the third form no test could produce a scope failure at all, so the defect it causes —
    // a bodyless 403 whose escaping exception was logged as a 500 — was unreachable from this suite;
    // the fourth exists for the same reason, since POST /travelers now reads the address it stores
    // from the token rather than from the request body.
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var authHeader = Request.Headers.Authorization.ToString();
        if (!authHeader.StartsWith("Bearer test-"))
            return Task.FromResult(AuthenticateResult.NoResult());

        var token = authHeader["Bearer test-".Length..];
        var isAdmin = token.StartsWith("admin-");
        var hasNoScope = token.StartsWith("noscope-");
        var hasNoEmail = token.StartsWith("noemail-");
        var userId = isAdmin
            ? token["admin-".Length..]
            : hasNoScope ? token["noscope-".Length..]
            : hasNoEmail ? token["noemail-".Length..] : token;

        var claims = new List<Claim> { new("oid", userId) };

        if (!hasNoEmail)
        {
            // preferred_username rather than email, because that is the claim the real Entra
            // External ID tenant emits — a captured dev token carries no "email" claim at all.
            var email = Request.Headers[EmailHeader].ToString();
            claims.Add(new Claim(
                "preferred_username",
                string.IsNullOrEmpty(email) ? $"{userId}@example.com" : email));
        }

        var displayName = Request.Headers[DisplayNameHeader].ToString();
        claims.Add(new Claim("name", string.IsNullOrEmpty(displayName) ? DisplayName : displayName));

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
