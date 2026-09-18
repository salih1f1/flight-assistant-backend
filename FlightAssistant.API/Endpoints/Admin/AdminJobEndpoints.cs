using FlightAssistant.API.HostedServices;
using FlightAssistant.Application.Commands.Admin.Jobs;
using FlightAssistant.Application.Jobs;
using FlightAssistant.Application.Queries.Admin.Jobs;
using FlightAssistant.Contracts.Requests.Admin;
using FlightAssistant.Contracts.Responses.Admin;
using MediatR;

namespace FlightAssistant.API.Endpoints.Admin
{
    /// <summary>
    /// <c>GET</c>/<c>PUT</c> for the two switchable background jobs (#471). Mapped onto the shared
    /// <c>/admin</c> group, which already carries <c>RequireAuthorization("AdminOnly")</c> and
    /// <c>RequireCors("AdminPanel")</c> — neither is re-applied here.
    ///
    /// <para>
    /// <b>Why this file, not <c>GetJobsQueryHandler</c>/<c>ToggleJobCommandHandler</c>, fills
    /// <c>LastOutcome</c>/<c>LastRunUtc</c>.</b> <c>FlightSyncHeartbeat</c> and
    /// <c>VerificationJobHeartbeat</c> are hosting artifacts that live in this layer with, per
    /// CLAUDE.md, "no cross-layer interface" — the same reason <c>AdminHealthEndpoints</c> reads them
    /// directly rather than through MediatR. Both Application handlers always leave those two fields
    /// null; <see cref="WithHeartbeat"/> is the one place that overwrites them with the live snapshot
    /// before a response goes out.
    /// </para>
    /// </summary>
    public static class AdminJobEndpoints
    {
        public static void MapAdminJobEndpoints(this IEndpointRouteBuilder admin)
        {
            var group = admin.MapGroup("jobs").WithTags("Admin - Jobs");

            // GET /admin/jobs
            group.MapGet("", async (
                IMediator mediator,
                FlightSyncHeartbeat heartbeat,
                VerificationJobHeartbeat verificationHeartbeat,
                CancellationToken ct) =>
            {
                var jobs = await mediator.Send(new GetJobsQuery(), ct);
                return Results.Ok(jobs.Select(j => WithHeartbeat(j, heartbeat, verificationHeartbeat)).ToList());
            })
            .WithName("AdminGetJobs")
            .WithSummary("The background jobs and whether they are running")
            .WithDescription(
                "Both scheduled jobs: the deployment config ceiling, the runtime switch, and the effective " +
                "state, alongside who last changed the switch and why. 'canToggle' is false when config has " +
                "the job off — config is a hard ceiling and no toggle can start a job it has disabled.")
            .Produces<List<AdminJobDto>>(200);

            // PUT /admin/jobs/{job}
            group.MapPut("{job}", async (
                string job,
                ToggleJobRequest request,
                IMediator mediator,
                FlightSyncHeartbeat heartbeat,
                VerificationJobHeartbeat verificationHeartbeat,
                CancellationToken ct) =>
            {
                var result = await mediator.Send(new ToggleJobCommand(job, request.Enabled, request.Reason), ct);
                return Results.Ok(WithHeartbeat(result, heartbeat, verificationHeartbeat));
            })
            .WithName("AdminToggleJob")
            .WithSummary("Switch a background job on or off")
            .WithDescription(
                "Stops or starts a job's scheduled loop, taking effect within about ten seconds. Manual " +
                "triggers are unaffected and keep working while a job is switched off. Audited. 409 when " +
                "config has the job off, 404 when the job name is unknown.")
            .Produces<AdminJobDto>(200)
            .ProducesProblem(404)
            .ProducesProblem(409);
        }

        private static AdminJobDto WithHeartbeat(
            AdminJobDto dto, FlightSyncHeartbeat heartbeat, VerificationJobHeartbeat verificationHeartbeat) => dto.Job switch
        {
            JobNames.FlightSync => dto with
            {
                LastOutcome = heartbeat.Current.LastOutcome.ToString(),
                LastRunUtc = heartbeat.Current.LastRunCompletedUtc ?? heartbeat.Current.LastRunStartedUtc,
            },
            JobNames.Verification => dto with
            {
                LastOutcome = verificationHeartbeat.Current.LastOutcome.ToString(),
                LastRunUtc = verificationHeartbeat.Current.LastRunCompletedUtc ?? verificationHeartbeat.Current.LastRunStartedUtc,
            },
            _ => dto
        };
    }
}
