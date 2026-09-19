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
            // Only ProducesValidationProblem, never it *and* ProducesProblem(400) (#506): OpenAPI
            // allows one entry per status and the later declaration wins, so the pair rendered this
            // 400 as a bare ProblemDetails and silently dropped the errors dictionary the route
            // actually returns. This operation really can produce both shapes -- its validator's
            // 400, and UsageRange.Resolve's DomainException(400) inverted-range backstop -- and
            // HttpValidationProblemDetails extends ProblemDetails, so declaring only this one is
            // the better of two imperfect options. A real oneOf is what this wants; Swashbuckle
            // will not emit one from these attributes.
            .ProducesValidationProblem()
            .ProducesProblem(500)
            .ProducesProblem(502)
            .ProducesProblem(503);

            // GET /admin/usage/flight-data
            group.MapGet("flight-data", async (
                DateTimeOffset? start,
                DateTimeOffset? end,
                IMediator mediator,
                CancellationToken ct) =>
            {
                var result = await mediator.Send(new GetFlightDataUsageQuery(start, end), ct);

                var dto = new FlightDataUsageDto(
                    result.RangeStartUtc,
                    result.RangeEndUtc,
                    result.TotalCalls,
                    result.TotalPages,
                    result.TotalCostUsd,
                    result.MonthlyBudgetUsd,
                    result.ByOperation
                        .Select(o => new FlightDataSpendOperationDto(o.Operation, o.Calls, o.Pages, o.CostUsd))
                        .ToList(),
                    result.ActiveTrackedFlights,
                    result.ByStoredSyncMode
                        .Select(m => new FlightDataSyncModeCountDto(m.Mode, m.Flights))
                        .ToList(),
                    result.TotalEventsRecorded,
                    result.EventYield
                        .Select(y => new FlightDataEventYieldDto(y.Source, y.Events, y.DistinctFlights))
                        .ToList(),
                    new FlightDataDerivedDto(
                        result.Derived.CostUsdPerActiveTrackedFlight,
                        result.Derived.CostUsdPerRecordedEvent));

                return Results.Ok(dto);
            })
            .WithName("AdminGetFlightDataUsage")
            .WithSummary("AeroAPI spend paired with our own sync activity")
            .WithDescription(
                "What #364's Phase 4 decisions need and GET /admin/usage/aeroapi cannot answer: " +
                "the same AeroAPI spend for the range, alongside how many distinct flights the " +
                "status loop is still polling, how those flights are distributed across sync modes, " +
                "and how many status events the window's polls actually produced. Optional 'start' " +
                "and 'end' are UTC instants defaulting to the current UTC month to date, resolved " +
                "and cached exactly as the aeroapi report is — so this costs no extra upstream " +
                "call. The 'derived' figures are calculated, not measured: AeroAPI exposes no " +
                "per-request identifier and bills per page, so nothing here attributes a page to a " +
                "flight; they are whole-window spend over a whole-window denominator, and are null " +
                "when that denominator is zero. 'byStoredSyncMode' reports each flight's stored " +
                "mode, which can be up to one loop interval stale, so its total may differ slightly " +
                "from 'activeTrackedFlights', which is recomputed from times and status.")
            .Produces<FlightDataUsageDto>(200)
            // Only ProducesValidationProblem, never it *and* ProducesProblem(400): OpenAPI allows
            // one entry per status and the later wins, so the pair silently drops the errors
            // dictionary. The route above declares both and loses it; this one does not repeat that.
            // HttpValidationProblemDetails extends ProblemDetails, so it covers UsageRange.Resolve's
            // own DomainException(400) backstop too.
            .ProducesValidationProblem()
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
