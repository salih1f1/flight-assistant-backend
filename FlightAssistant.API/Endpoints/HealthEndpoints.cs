namespace FlightAssistant.API.Endpoints
{
    public static class HealthEndpoints
    {
        public static void MapHealthEndpoints(this IEndpointRouteBuilder app)
        {
            var group = app.MapGroup("health");

            group.MapGet("", () => Results.Ok(new { status = "ok", env = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") }));
        }
    }
}
