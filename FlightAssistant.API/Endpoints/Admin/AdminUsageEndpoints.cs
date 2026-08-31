using FlightAssistant.Application.Queries.Admin.Usage;
using FlightAssistant.Contracts.Responses.Admin;
using MediatR;

namespace FlightAssistant.API.Endpoints.Admin
{
    public static class AdminUsageEndpoints
    {
        public static void MapAdminUsageEndpoints(this IEndpointRouteBuilder admin)
        {
            var group = admin.MapGroup("usage")
                .WithTags("Admin - Usage");

            // GET /admin/usage/aeroapi
            group.MapGet("aeroapi", async ([AsParameters] GetAeroApiUsageQuery query, IMediator mediator, CancellationToken ct) =>
            {
                var result = await mediator.Send(query, ct);
                var dto = new AeroApiUsageDto(
                    result.FromUtc,
                    result.ToUtc,
                    result.TotalCalls,
                    result.EstimatedSpendUsd,
                    result.MonthlyBudgetUsd,
                    result.CurrentMonthCalls,
                    result.CurrentMonthSpendUsd,
                    result.ByTemplate.Select(t => new AeroApiUsageTemplateBreakdownDto(
                        t.EndpointTemplate, t.Calls, t.SpendUsd, t.Share)).ToList(),
                    result.ByOutcome.Select(o => new AeroApiUsageOutcomeBreakdownDto(
                        o.Outcome, o.Calls)).ToList(),
                    result.DailyCounts.Select(d => new AeroApiUsageDailyCountDto(
                        d.Date, d.Calls)).ToList(),
                    result.Status429CountLast7Days,
                    result.Status502CountLast7Days,
                    result.NextSlotUtc);
                return Results.Ok(dto);
            })
            .WithName("AdminGetAeroApiUsage")
            .WithSummary("AeroAPI usage rollup: calls, estimated spend and budget over a UTC date range")
            .Produces<AeroApiUsageDto>(200)
            .ProducesValidationProblem();
        }
    }
}
