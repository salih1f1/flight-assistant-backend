using Microsoft.Identity.Web.Resource;
using System.Security.Claims;

namespace FlightAssistant.API.Endpoints
{
    public static class PrivateEndpoints
    {
        private const string ScopeAccessAsUser = "access_as_user";

        public static void MapPrivateEndpoints(this IEndpointRouteBuilder app)
        {
            var group = app.MapGroup("private").WithTags("Private");

            // GET /private/ping — unauthenticated health check
            group.MapGet("ping", () => "pong")
                .WithName("Ping")
                .WithSummary("Ping")
                .Produces<string>(200);

            // GET /private/secure-test — verifies token is accepted
            group.MapGet("secure-test", (HttpContext http) =>
            {
                return Results.Ok(new { ok = true, at = DateTime.UtcNow });
            })
            .WithName("SecureTest")
            .WithSummary("Authenticated ping")
            .Produces(200)
            .RequireAuthorization("AccessAsUser");

            // GET /private/whoami — returns decoded token claims
            group.MapGet("whoami", (HttpContext http) =>
            {
                return Results.Ok(new
                {
                    name = http.User.Identity?.Name,
                    aud  = http.User.FindFirstValue("aud"),
                    iss  = http.User.FindFirstValue("iss"),
                    tfp  = http.User.FindFirst("tfp")?.Value ?? http.User.FindFirst("acr")?.Value
                });
            })
            .WithName("WhoAmI")
            .WithSummary("Return decoded token claims")
            .Produces(200)
            .RequireAuthorization("AccessAsUser");
        }
    }
}
