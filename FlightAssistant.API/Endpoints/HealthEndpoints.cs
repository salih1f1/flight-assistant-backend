using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FlightAssistant.API.Endpoints
{
    public static class HealthEndpoints
    {
        public static void MapHealthEndpoints(this IEndpointRouteBuilder app)
        {
            // GET /health/live — always 200 while process is running; used by App Service restart policy
            app.MapHealthChecks("health/live", new HealthCheckOptions
            {
                Predicate = _ => false, // no checks — pure liveness
                ResultStatusCodes =
                {
                    [HealthStatus.Healthy]   = StatusCodes.Status200OK,
                    [HealthStatus.Degraded]  = StatusCodes.Status200OK,
                    [HealthStatus.Unhealthy] = StatusCodes.Status200OK,
                }
            }).AllowAnonymous().WithTags("Health");

            // GET /health/ready — checks DB + Redis; excludes external dependencies
            app.MapHealthChecks("health/ready", new HealthCheckOptions
            {
                Predicate = check => !check.Tags.Contains("external"),
                ResultStatusCodes =
                {
                    [HealthStatus.Healthy]   = StatusCodes.Status200OK,
                    [HealthStatus.Degraded]  = StatusCodes.Status200OK,
                    [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable,
                }
            }).AllowAnonymous().WithTags("Health");
        }
    }
}
