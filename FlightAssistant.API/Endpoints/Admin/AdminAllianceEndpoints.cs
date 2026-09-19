using FlightAssistant.Application.Queries.Admin.RefData;
using FlightAssistant.Contracts.Responses.Admin;
using MediatR;

namespace FlightAssistant.API.Endpoints.Admin
{
    public static class AdminAllianceEndpoints
    {
        public static void MapAdminAllianceEndpoints(this IEndpointRouteBuilder admin)
        {
            var group = admin.MapGroup("alliances")
                .WithTags("Admin - Alliances");

            // GET /admin/alliances/{id}/detail — the alliance with the airlines that belong to it.
            // Keyed by Id, matching PATCH /admin/alliances/{id}/verify and the data browser's own
            // record key for this table, not by Code — even though Code is what the airlines join on.
            group.MapGet("{id:int}/detail", async (int id, IMediator mediator, CancellationToken ct) =>
            {
                var result = await mediator.Send(new GetAdminAllianceDetailQuery(id), ct);
                if (result is null) return Results.NotFound();
                return Results.Ok(new AllianceDetailAdminDto(
                    result.Id, result.Code, result.Name,
                    result.Airlines
                        .Select(a => new AllianceAirlineDto(
                            a.Id, a.Iata, a.Icao, a.Name, a.CountryCode, a.LoyaltyProgramName))
                        .ToList()));
            })
            .WithName("AdminGetAllianceDetail")
            .WithSummary("Get alliance detail")
            .WithDescription(
                "Returns the alliance together with every airline whose AllianceCode is its Code, " +
                "ordered by name, each with the name of the airline's own loyalty program (null " +
                "when it runs none). The list is not capped: it is bounded by how many airlines exist " +
                "in the alliance rather than growing with usage, so there is no truncation flag. " +
                "An alliance no airline belongs to is a 200 with an empty list, not a 404. " +
                "Verification state is deliberately absent — it lives with the raw record at " +
                "GET /admin/data/alliances.")
            .Produces<AllianceDetailAdminDto>(200)
            .ProducesProblem(404);
        }
    }
}
