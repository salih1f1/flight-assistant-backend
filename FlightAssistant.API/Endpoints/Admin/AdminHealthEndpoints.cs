using FlightAssistant.API.HostedServices;
using FlightAssistant.Contracts.Responses.Admin;
using FlightAssistant.Infrastructure.Persistance;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FlightAssistant.API.Endpoints.Admin
{
    public static class AdminHealthEndpoints
    {
        /// <summary>
        /// Mapped onto the shared <c>/admin</c> group, which already carries
        /// <c>RequireAuthorization("AdminOnly")</c> and <c>RequireCors("AdminPanel")</c> — neither is
        /// re-applied here.
        ///
        /// <para>
        /// <b>Deliberately not routed through MediatR</b>, unlike <c>/admin/usage/aeroapi</c>
        /// immediately beside it. Every input this endpoint needs —
        /// <see cref="HealthCheckService"/>, <see cref="IHostEnvironment"/>,
        /// <see cref="IConfiguration"/>, the entry assembly, the <c>DbContext</c>'s migration lists —
        /// is a hosting artifact, not domain or application state. Wrapping four of them in
        /// Application-side translation interfaces just to satisfy CQRS would be ceremony with
        /// nothing testable on the other side; <c>HealthEndpoints</c> (<c>/health/live</c>,
        /// <c>/health/ready</c>) is already mapped directly in this layer the same way. The shaping
        /// logic itself lives in <see cref="AdminHealthReportBuilder"/>, which is unit-testable
        /// without a running host.
        /// </para>
        /// </summary>
        public static void MapAdminHealthEndpoints(this IEndpointRouteBuilder admin)
        {
            // GET /admin/health
            admin.MapGet("health", async (
                HealthCheckService healthCheckService,
                FlightAssistantDbContext db,
                FlightSyncHeartbeat heartbeat,
                IHostEnvironment env,
                IConfiguration configuration,
                CancellationToken ct) =>
            {
                var dto = await AdminHealthReportBuilder.BuildAsync(
                    healthCheckService, db, heartbeat, env, configuration, ct);

                // Always 200, even when Status is "Unhealthy": this is a report, not a probe. A
                // non-2xx response would make the panel's HTTP error handling swallow the very body
                // that explains what broke — the overall status lives in the payload instead.
                return Results.Ok(dto);
            })
            .WithTags("Admin - Health")
            .WithName("AdminGetHealth")
            .WithSummary("Operator health detail")
            .WithDescription(
                "Runs every registered health check with no predicate — including 'aeroapi' and " +
                "'apns', tagged 'external' and excluded from the load-balancer probe at " +
                "/health/ready — and always responds 200, even when the overall status is " +
                "Unhealthy. Also reports process/build info (version, build SHA, uptime), pending " +
                "EF Core migrations, and the flight-status sync's last-run heartbeat. Exception " +
                "detail on individual checks and the sync's last error render outside Production " +
                "only.")
            .Produces<AdminHealthDto>(200);
        }
    }
}
