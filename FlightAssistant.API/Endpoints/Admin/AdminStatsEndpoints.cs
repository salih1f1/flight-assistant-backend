using FlightAssistant.Application.Queries.Admin.Stats;
using FlightAssistant.Contracts.Responses.Admin;
using MediatR;

namespace FlightAssistant.API.Endpoints.Admin
{
    public static class AdminStatsEndpoints
    {
        /// <summary>
        /// Mapped onto the shared <c>/admin</c> group, which already carries
        /// <c>RequireAuthorization("AdminOnly")</c> and <c>RequireCors("AdminPanel")</c> — neither
        /// is re-applied here.
        /// </summary>
        public static void MapAdminStatsEndpoints(this IEndpointRouteBuilder admin)
        {
            var group = admin.MapGroup("stats")
                .WithTags("Admin - Stats");

            // GET /admin/stats
            group.MapGet("", async (string? period, IMediator mediator, CancellationToken ct) =>
            {
                var result = await mediator.Send(new GetAdminStatsQuery(period), ct);

                return Results.Ok(new AdminStatsDto(
                    result.FromUtc,
                    result.ToUtc,
                    result.Period,
                    result.Bucket,
                    result.Entities
                        .Select(e => new AdminEntityStatsDto(
                            e.Key, e.DateColumn, e.Total, e.NewInPeriod, e.Verified, e.Unverified, e.Series))
                        .ToList()));
            })
            .WithName("AdminGetStats")
            .WithSummary("Dashboard card counts: totals, new in a period, verified")
            .WithDescription(
                "For travelers, flights, airlines, airports, lounges and loyalty programs: the total, " +
                "how many were added in the period with a per-bucket series, and verified/unverified " +
                "counts (null for travelers and flights). 'period' is 24h, 7d, 30d or 90d (default 7d), " +
                "resolved to whole hourly (24h) or UTC-day buckets ending with the current one, so " +
                "NewInPeriod always equals the sum of Series. 'Added' is CreatedUtc, or FirstSeenAt for " +
                "airlines and airports; rows with no known date and seeded airlines and airports never " +
                "count as new.")
            .Produces<AdminStatsDto>(200)
            .ProducesValidationProblem();
        }
    }
}
