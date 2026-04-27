using FlightAssistant.Application.Commands.Admin.Lounges;
using FlightAssistant.Application.Queries.Admin.Lounges;
using FlightAssistant.Contracts.Paging;
using FlightAssistant.Contracts.Requests.Admin.Lounges;
using FlightAssistant.Contracts.Responses.Admin;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace FlightAssistant.API.Endpoints.Admin
{
    public static class AdminLoungeEndpoints
    {
        public static void MapAdminLoungeEndpoints(this IEndpointRouteBuilder admin)
        {
            var group = admin.MapGroup("lounges")
                .WithTags("Admin - Lounges");

            // GET /admin/lounges
            group.MapGet("", async ([AsParameters] PagedRequest paging, IMediator mediator, CancellationToken ct) =>
            {
                var result = await mediator.Send(new GetAllLoungesAdminQuery(paging), ct);
                var dtos = result.Items.Select(l => new AdminLoungeDto(
                    l.Id, l.AirportId, l.Name, l.Terminal, l.LocationHint, l.Hours,
                    l.IsAirside, l.IsDomesticOnly, l.IsInternationalOnly, l.Amenities)).ToList();
                return Results.Ok(new PagedResult<AdminLoungeDto>(dtos, result.Total, result.Page, result.PageSize));
            })
            .WithName("AdminGetAllLounges")
            .WithSummary("List all lounges")
            .Produces<PagedResult<AdminLoungeDto>>(200)
            .ProducesValidationProblem();

            // GET /admin/lounges/{id}
            group.MapGet("{id:int}", async (int id, IMediator mediator, CancellationToken ct) =>
            {
                var l = await mediator.Send(new GetAdminLoungeByIdQuery(id), ct);
                if (l is null) return Results.NotFound();
                return Results.Ok(new AdminLoungeDto(l.Id, l.AirportId, l.Name, l.Terminal, l.LocationHint, l.Hours,
                    l.IsAirside, l.IsDomesticOnly, l.IsInternationalOnly, l.Amenities));
            })
            .WithName("AdminGetLoungeById")
            .WithSummary("Get lounge by ID")
            .Produces<AdminLoungeDto>(200)
            .ProducesProblem(404);

            // POST /admin/lounges
            group.MapPost("", async ([FromBody] CreateLoungeRequest req, IMediator mediator, CancellationToken ct) =>
            {
                var id = await mediator.Send(new CreateLoungeCommand(
                    req.AirportId, req.Name, req.Terminal, req.LocationHint, req.Hours,
                    req.IsAirside, req.IsDomesticOnly, req.IsInternationalOnly, req.Amenities), ct);
                return Results.Created($"/admin/lounges/{id}", new { id });
            })
            .WithName("AdminCreateLounge")
            .WithSummary("Create lounge")
            .Produces(201)
            .ProducesValidationProblem();

            // PUT /admin/lounges/{id}
            group.MapPut("{id:int}", async (int id, [FromBody] UpdateLoungeRequest req, IMediator mediator, CancellationToken ct) =>
            {
                await mediator.Send(new UpdateLoungeCommand(id, req.Name, req.Terminal, req.LocationHint, req.Hours,
                    req.IsAirside, req.IsDomesticOnly, req.IsInternationalOnly, req.Amenities), ct);
                return Results.NoContent();
            })
            .WithName("AdminUpdateLounge")
            .WithSummary("Update lounge")
            .Produces(204)
            .ProducesProblem(404);

            // DELETE /admin/lounges/{id}
            group.MapDelete("{id:int}", async (int id, IMediator mediator, CancellationToken ct) =>
            {
                await mediator.Send(new DeleteLoungeCommand(id), ct);
                return Results.NoContent();
            })
            .WithName("AdminDeleteLounge")
            .WithSummary("Delete lounge")
            .Produces(204);
        }
    }
}
