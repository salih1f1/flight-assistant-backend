using FlightAssistant.Application.Queries.Admin.Usage;
using FlightAssistant.Contracts.Responses.Admin;
using MediatR;

namespace FlightAssistant.API.Endpoints.Admin
{
    public static class AdminUsageEndpoints
    {
        /// <summary>
        /// Mapped onto the shared <c>/admin</c> group, which already carries
        /// <c>RequireAuthorization("AdminOnly")</c> and <c>RequireCors("AdminPanel")</c> — neither
        /// is re-applied here.
        /// </summary>
        public static void MapAdminUsageEndpoints(this IEndpointRouteBuilder admin)
        {
            var group = admin.MapGroup("usage")
                .WithTags("Admin - Usage");

            // GET /admin/usage/aeroapi
            group.MapGet("aeroapi", async (
                DateTimeOffset? start,
                DateTimeOffset? end,
                IMediator mediator,
                CancellationToken ct) =>
            {
                var result = await mediator.Send(new GetAeroApiUsageQuery(start, end), ct);

                var dto = new AeroApiUsageDto(
                    result.RangeStartUtc,
                    result.RangeEndUtc,
                    result.TotalCalls,
                    result.TotalPages,
                    result.TotalCostUsd,
                    result.TotalDiscountCostUsd,
                    result.TotalSuccessfulCalls,
                    result.TotalFailedCalls,
                    result.MonthlyBudgetUsd,
                    result.ByOperation
                        .Select(o => new AeroApiUsageOperationDto(
                            o.Operation, o.Calls, o.Pages, o.CostUsd, o.SuccessfulCalls, o.FailedCalls))
                        .ToList(),
                    result.NextSlotUtc);

                return Results.Ok(dto);
            })
            .WithName("AdminGetAeroApiUsage")
            .WithSummary("AeroAPI account usage and spend")
            .WithDescription(
                "Proxies FlightAware's own GET /account/usage report — calls, pages and cost for " +
                "the range, broken down by operation, alongside the configured monthly budget and " +
                "the next available outbound throttle slot. Optional 'start' and 'end' are UTC " +
                "instants; they default to the current UTC month to date. AeroAPI serves at most " +
                "one year of history, so an earlier start is rejected. Cached for 10 minutes, " +
                "matching FlightAware's own refresh cadence.")
            .Produces<AeroApiUsageDto>(200)
            .ProducesValidationProblem()
            // A plain ProblemDetails 400 as well as the ValidationProblemDetails one above:
            // UsageRange.Resolve throws DomainException(400) as a backstop if a resolved range
            // is ever inverted, which GlobalExceptionHandler renders without an errors
            // dictionary. Same shape-at-one-status split AdminImportEndpoints declares.
            .ProducesProblem(400)
            .ProducesProblem(500)
            .ProducesProblem(502)
            .ProducesProblem(503);

            // GET /admin/usage/azure
            group.MapGet("azure", async (IMediator mediator, CancellationToken ct) =>
                Results.Ok(await mediator.Send(new GetAzureBudgetQuery(), ct)))
            .WithName("AdminGetAzureBudget")
            .WithSummary("Azure subscription budget and month-to-date spend")
            .WithDescription(
                "The subscription's budget as defined in Azure Cost Management, with spend against " +
                "it. 'state' says what the API could actually read: Ok, NoBudget when none is " +
                "defined, NotConfigured when no subscription is set, NotAuthorized when the managed " +
                "identity lacks the Cost Management Reader role, or Unavailable. Azure reports cost " +
                "on a delay of roughly 8-24 hours, but neither Cost Management endpoint behind this " +
                "reports its own freshness, so 'dataAsOfUtc' is always null today — the delay is " +
                "real, just unmeasurable from the response. Amounts are in 'currency', which is the " +
                "subscription's billing currency and not necessarily USD. Cached for an hour, " +
                "except refusals and outages, which are not cached.")
            // Always 200: every failure is a state in the body, because the panel must render
            // "not authorized" as information rather than as a failed request — unlike AeroAPI
            // above, there is no validation to reject and no upstream error this passes through.
            .Produces<AzureBudgetDto>(200);
        }
    }
}
