namespace FlightAssistant.API.Endpoints
{
    public static class HealthEndpoints
    {
        public static void MapHealthEndpoints(this IEndpointRouteBuilder app)
        {
            var group = app.MapGroup("health").WithTags("Health");

            group.MapGet("", () => Results.Ok(new { status = "ok", env = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") }))
                .WithName("GetHealth")
                .WithSummary("Health check")
                .WithDescription("Returns the current API status and the hosting environment name. No authentication required.")
                .Produces(200)
                .AllowAnonymous();
        }
    }
}
