using FlightAssistant.API.HostedServices;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Application.Queries.Admin.DataSources;
using FlightAssistant.Contracts.Responses.Admin;
using FlightAssistant.Infrastructure.Persistance;
using MediatR;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

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
                VerificationJobHeartbeat verificationHeartbeat,
                IOptions<VerificationOptions> verificationOptions,
                IVerificationRequestRepository verificationRequests,
                IHostEnvironment env,
                IConfiguration configuration,
                ILoggerFactory loggerFactory,
                CancellationToken ct) =>
            {
                // AdminHealthReportBuilder is a static class and so cannot itself be an ILogger<T>
                // category — a plain string category (its own full name) sidesteps that without
                // introducing a marker type just for this.
                var logger = loggerFactory.CreateLogger(
                    "FlightAssistant.API.Endpoints.Admin.AdminHealthReportBuilder");

                var dto = await AdminHealthReportBuilder.BuildAsync(
                    healthCheckService, db, heartbeat, verificationHeartbeat, verificationOptions, verificationRequests,
                    env, configuration, logger, ct);

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
                "EF Core migrations, the flight-status sync's last-run heartbeat, and the reference " +
                "verification job's last-cycle heartbeat with its pending request count. Exception " +
                "detail on individual checks and both jobs' last errors render outside Production " +
                "only.")
            .Produces<AdminHealthDto>(200)
            // Every known failure mode degrades gracefully into the 200 payload (including a
            // database outage — final review C1); this stays reachable for anything unexpected,
            // e.g. HealthCheckService itself throwing.
            .ProducesProblem(500);

            // GET /admin/health/sources
            admin.MapGet("health/sources", async (IMediator mediator, IHostEnvironment env, CancellationToken ct) =>
                // Same non-Production rule the report above applies to its own diagnostics: a
                // sanitized LastError is still exception-shaped detail.
                Results.Ok(await mediator.Send(new GetDataSourcesQuery(IncludeDiagnostics: !env.IsProduction()), ct)))
            .WithTags("Admin - Health")
            .WithName("AdminGetDataSources")
            .WithSummary("Every outside data source and its observed status")
            .WithDescription(
                "One row per catalogued dependency: its current status, when it was last seen working, when " +
                "it was last observed at all, a sentence saying what that status is based on, and where to " +
                "check it yourself. Reads a small table and runs no checks — unlike GET /admin/health, which " +
                "runs every registered health check live. A source that has never been observed is reported " +
                "as Unknown, never as healthy.")
            .Produces<List<AdminDataSourceDto>>(200);
        }
    }
}
