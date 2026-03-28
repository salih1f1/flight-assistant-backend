using FlightAssistant.Application.Commands.Admin.Travelers;
using FlightAssistant.Application.Queries.Admin.Travelers;
using FlightAssistant.Contracts.Responses.Admin;
using MediatR;

namespace FlightAssistant.API.Endpoints.Admin
{
    public static class AdminTravelerEndpoints
    {
        public static void MapAdminTravelerEndpoints(this IEndpointRouteBuilder app)
        {
            var group = app.MapGroup("admin/travelers")
                .RequireAuthorization("AdminOnly")
                .WithTags("Admin - Travelers");

            // GET /admin/travelers
            group.MapGet("", async (IMediator mediator, CancellationToken ct) =>
            {
                var results = await mediator.Send(new GetAllTravelersQuery(), ct);
                return Results.Ok(results.Select(t => new AdminTravelerDto(
                    t.Id, t.Email, t.DisplayName, t.CountryCode,
                    t.NationalityCountryCode, t.ResidenceCountryCode, t.CreatedUtc)));
            })
            .WithName("AdminGetAllTravelers")
            .WithSummary("List all travelers")
            .Produces<IEnumerable<AdminTravelerDto>>(200);

            // GET /admin/travelers/{id}
            group.MapGet("{id:guid}", async (Guid id, IMediator mediator, CancellationToken ct) =>
            {
                var result = await mediator.Send(new GetAdminTravelerByIdQuery(id), ct);
                if (result is null) return Results.NotFound();
                return Results.Ok(new AdminTravelerDto(
                    result.Id, result.Email, result.DisplayName, result.CountryCode,
                    result.NationalityCountryCode, result.ResidenceCountryCode, result.CreatedUtc));
            })
            .WithName("AdminGetTravelerById")
            .WithSummary("Get traveler by ID")
            .Produces<AdminTravelerDto>(200)
            .ProducesProblem(404);

            // DELETE /admin/travelers/{id}
            group.MapDelete("{id:guid}", async (Guid id, IMediator mediator, CancellationToken ct) =>
            {
                await mediator.Send(new DeleteTravelerCommand(id), ct);
                return Results.NoContent();
            })
            .WithName("AdminDeleteTraveler")
            .WithSummary("Delete traveler")
            .Produces(204);
        }
    }
}
