using FlightAssistant.Application.Commands.Admin.Travelers;
using FlightAssistant.Application.Queries.Admin.Travelers;
using FlightAssistant.Contracts.Paging;
using FlightAssistant.Contracts.Responses.Admin;
using MediatR;

namespace FlightAssistant.API.Endpoints.Admin
{
    public static class AdminTravelerEndpoints
    {
        public static void MapAdminTravelerEndpoints(this IEndpointRouteBuilder admin)
        {
            var group = admin.MapGroup("travelers")
                .WithTags("Admin - Travelers");

            // GET /admin/travelers
            group.MapGet("", async ([AsParameters] PagedRequest paging, IMediator mediator, CancellationToken ct) =>
            {
                var result = await mediator.Send(new GetAllTravelersQuery(paging), ct);
                var dtos = result.Items.Select(t => new AdminTravelerDto(
                    t.Id, t.Email, t.DisplayName, t.CountryCode,
                    t.NationalityCountryCode, t.ResidenceCountryCode, t.CreatedUtc)).ToList();
                return Results.Ok(new PagedResult<AdminTravelerDto>(dtos, result.Total, result.Page, result.PageSize));
            })
            .WithName("AdminGetAllTravelers")
            .WithSummary("List all travelers")
            .Produces<PagedResult<AdminTravelerDto>>(200)
            .ProducesValidationProblem();

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
