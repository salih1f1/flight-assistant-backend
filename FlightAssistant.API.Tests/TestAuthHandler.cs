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

    // Accepts "Bearer test-{guid}" for users and "Bearer test-admin-{guid}" for admins.
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var authHeader = Request.Headers.Authorization.ToString();
        if (!authHeader.StartsWith("Bearer test-"))
            return Task.FromResult(AuthenticateResult.NoResult());

        var token = authHeader["Bearer test-".Length..];
        var isAdmin = token.StartsWith("admin-");
        var userId = isAdmin ? token["admin-".Length..] : token;

        var claims = new List<Claim>
        {
            new("oid", userId),
            // Identity profile claims the API derives the traveler from (mirrors a B2C token).
            new("emails", $"{userId}@example.com"),
            new("name", "Test User"),
            new("country", "GB"),
            // Both scope claim forms for compatibility with VerifyUserHasAnyAcceptedScope
            new("scp", "access_as_user"),
            new("http://schemas.microsoft.com/identity/claims/scope", "access_as_user"),
        };

        if (isAdmin)
            claims.Add(new Claim("roles", "admin"));

        var identity = new ClaimsIdentity(claims, SchemeName, nameType: "name", roleType: "roles");
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
