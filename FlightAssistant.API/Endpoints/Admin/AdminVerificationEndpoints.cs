using System.Security.Claims;
using FlightAssistant.API.Authentication;
using FlightAssistant.Application.Commands.Admin.Verification;
using FlightAssistant.Application.Queries.Admin.Verification;
using FlightAssistant.Contracts.Requests.Admin;
using FlightAssistant.Contracts.Responses.Admin;
using MediatR;

namespace FlightAssistant.API.Endpoints.Admin
{
    /// <summary>
    /// Admin-started reference-data verification (#378, spec §4.1, §4.4): list the sources, queue checks,
    /// accept a source's values for a mismatch, and retry. Mapped onto the shared <c>/admin</c> group, which
    /// already carries <c>RequireAuthorization("AdminOnly")</c> and <c>RequireCors("AdminPanel")</c>; neither
    /// is re-applied here. Every rule and status lives in the Application handlers and validators; these
    /// endpoints bind, resolve the actor, send, and map the result.
    /// </summary>
    public static class AdminVerificationEndpoints
    {
        private const string Tag = "Admin - Verification";

        public static void MapAdminVerificationEndpoints(this IEndpointRouteBuilder admin)
        {
            // GET /admin/verification-sources?entityType=Airport
            admin.MapGet("verification-sources", async (string? entityType, IMediator mediator, CancellationToken ct) =>
            {
                var sources = await mediator.Send(new GetVerificationSourcesQuery(entityType), ct);
                return Results.Ok(sources.Select(s => new VerificationSourceDto(s.Name, s.CostPerRowUsd)).ToList());
            })
            .WithTags(Tag)
            .WithName("AdminListVerificationSources")
            .WithSummary("List the verification sources that can check an entity type")
            .WithDescription(
                "The registered sources whose supported entity types include 'entityType', ordered by name, with " +
                "each one's cost per row lookup. 'entityType' is required and must be exactly Airport, Airline or " +
                "Country; anything else is a 400. An empty list is a valid answer: no source checks Airline yet.")
            .Produces<List<VerificationSourceDto>>(200)
            .ProducesValidationProblem();

            // GET /admin/verification-needs-attention
            admin.MapGet("verification-needs-attention", async (IMediator mediator, CancellationToken ct) =>
            {
                var counts = await mediator.Send(new GetVerificationNeedsAttentionQuery(), ct);
                return Results.Ok(counts.Select(c => new VerificationNeedsAttentionDto(c.EntityType.ToString(), c.Count)).ToList());
            })
            .WithTags(Tag)
            .WithName("AdminGetVerificationNeedsAttention")
            .WithSummary("Counts of reference-data rows that need attention")
            .WithDescription(
                "One entry per VerificationEntityType (Airport, Airline, Country), always all three, in " +
                "enum order, with a count of 0 rather than an omitted entry. A row needs attention when " +
                "its most recent automated check found a problem: IsVerified is false and " +
                "VerificationIssue is set -- the state only the verification job's mismatch and " +
                "not-found paths leave behind. A row that has simply never been checked (IsVerified " +
                "false, no issue) does not count. No parameters.")
            .Produces<List<VerificationNeedsAttentionDto>>(200);

            var requests = admin.MapGroup("verification-requests").WithTags(Tag);

            // GET /admin/verification-requests?entityType=Airport&entityKey=1&limit=5
            requests.MapGet("", async (
                string? entityType, string? entityKey, int? limit, IMediator mediator, CancellationToken ct) =>
            {
                var results = await mediator.Send(new GetVerificationRequestsQuery(entityType, entityKey, limit), ct);
                return Results.Ok(results.Select(ToDto).ToList());
            })
            .WithName("AdminListVerificationRequests")
            .WithSummary("An entity's most recent verification checks")
            .WithDescription(
                "The entity's checks, newest first, at most 'limit' (default 5, range 1-50). Not a page: no " +
                "total, and the full history stays browsable at GET /admin/data/verification-requests. Each " +
                "row's sources, differences and source values come back typed, not as the stored comma-joined " +
                "or JSON strings: sources is the list of source names checked; differences is { column, ours, " +
                "source, sourceName } per differing column; sourceValues is keyed by source name, each value " +
                "a map of column name to what that source reported. " +
                "'entityType' is required and must be exactly Airport, Airline or Country; 'entityKey' is " +
                "required and canonicalised before matching, so a lower-case country code finds the " +
                "upper-case row. An entity with no checks yet is an empty list, never a 404.")
            .Produces<List<VerificationRequestDto>>(200)
            .ProducesValidationProblem();

            // POST /admin/verification-requests
            requests.MapPost("", async (
                QueueVerificationRequestsRequest request, ClaimsPrincipal user, IMediator mediator, CancellationToken ct) =>
            {
                var result = await mediator.Send(
                    new QueueVerificationRequestsCommand(request.EntityType, request.Keys, request.Sources, user.GetAdminActorId()), ct);
                return Results.Accepted(uri: null, new VerificationEnqueueDto(result.Queued, result.Skipped));
            })
            .WithName("AdminQueueVerificationRequests")
            .WithSummary("Queue verification checks for reference-data rows")
            .WithDescription(
                "Queues one Pending request per key, checked against every selected source, with trigger 'admin', " +
                "the caller as requested_by, and the selected sources' summed per-row cost as the estimated cost. " +
                "Keys are not checked for existence: a missing entity completes as Error when the job runs it. " +
                "A repeated key, or an entity that already has an open request, is counted as skipped. " +
                "400 when entityType is not Airport, Airline or Country; keys or sources are empty; there are more " +
                "than 1000 keys; a key is malformed for the entity type (the error names it, e.g. 'Keys[1]'); or a " +
                "source is unknown or does not support the entity type. Queue rows are not audited; requested_by " +
                "records who asked.")
            .Produces<VerificationEnqueueDto>(202)
            .ProducesValidationProblem();

            // POST /admin/verification-requests/{id}/accept-source
            requests.MapPost("{id:long}/accept-source", async (
                long id, AcceptVerificationSourceRequest request, ClaimsPrincipal user, IMediator mediator, CancellationToken ct) =>
            {
                await mediator.Send(new AcceptVerificationSourceCommand(id, request.Source, user.GetAdminActorId()), ct);
                return Results.NoContent();
            })
            .WithName("AdminAcceptVerificationSource")
            .WithSummary("Accept one source's values for a mismatched check")
            .WithDescription(
                "Writes the chosen source's values to the entity for only the columns that source reported as " +
                "different, verifies the entity with that source as the caller, and saves once. The entity write is " +
                "audited as an Update; the request itself stays Mismatch. " +
                "400 when 'source' is blank, is not one of the request's sources, or has no stored values. " +
                "404 when the request or the entity no longer exists. " +
                "409 when the request is not Mismatch; the entity has an open verification request (accept once it " +
                "has finished); a differing column has changed since the check; a value cannot be parsed or would " +
                "empty a required column; the row would break the admin edit's rules; a new unique code already " +
                "belongs to another row; or a new country code is not one of our countries. The edit-rules check " +
                "validates the whole row, as the admin edit does, so an existing invalid value in a column the " +
                "source did not touch also returns 409, naming that column.")
            .Produces(204)
            // ValidationProblemDetails also describes the handler's plain ProblemDetails 400s; a separate
            // ProducesProblem(400) would replace it in the spec.
            .ProducesValidationProblem()
            .ProducesProblem(404)
            .ProducesProblem(409);

            // POST /admin/verification-requests/{id}/retry
            requests.MapPost("{id:long}/retry", async (long id, ClaimsPrincipal user, IMediator mediator, CancellationToken ct) =>
            {
                var result = await mediator.Send(new RetryVerificationRequestCommand(id, user.GetAdminActorId()), ct);
                return Results.Accepted(uri: null, new VerificationEnqueueDto(result.Queued, result.Skipped));
            })
            .WithName("AdminRetryVerificationRequest")
            .WithSummary("Check an entity again with an earlier request's sources")
            .WithDescription(
                "Queues a new Pending request for the same entity type, key and sources, with trigger 'admin', the " +
                "caller as requested_by, and the sources' current cost. The earlier request is left as it is. " +
                "400 when one of its sources is no longer registered or no longer supports the entity type. " +
                "404 when the request does not exist. 409 when the entity already has an open request.")
            .Produces<VerificationEnqueueDto>(202)
            // As on accept-source: the one 400 entry must stay ValidationProblemDetails.
            .ProducesValidationProblem()
            .ProducesProblem(404)
            .ProducesProblem(409);
        }

        private static VerificationRequestDto ToDto(VerificationRequestResult r) => new(
            r.Id,
            r.EntityType.ToString(),
            r.EntityKey,
            r.Sources,
            r.Trigger.ToString(),
            r.Status.ToString(),
            r.Attempts,
            r.NextAttemptUtc,
            r.Differences.Select(d => new VerificationRequestDifferenceDto(d.Column, d.Ours, d.Source, d.SourceName)).ToList(),
            r.SourceValues,
            r.Error,
            r.EstimatedCostUsd,
            r.RequestedBy,
            r.RequestedUtc,
            r.StartedUtc,
            r.CompletedUtc);
    }
}
