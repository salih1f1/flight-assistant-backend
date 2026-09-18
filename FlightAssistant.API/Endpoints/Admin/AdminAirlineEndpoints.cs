using FlightAssistant.Application.Commands.Admin.Airlines;
using FlightAssistant.Application.Queries.Admin.Airlines;
using FlightAssistant.Contracts.Paging;
using FlightAssistant.Contracts.Requests.Admin.Airlines;
using FlightAssistant.Contracts.Responses.Admin;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace FlightAssistant.API.Endpoints.Admin
{
    public static class AdminAirlineEndpoints
    {
        public static void MapAdminAirlineEndpoints(this IEndpointRouteBuilder admin)
        {
            var group = admin.MapGroup("airlines")
                .WithTags("Admin - Airlines");

            // GET /admin/airlines
            group.MapGet("", async ([AsParameters] PagedRequest paging, IMediator mediator, CancellationToken ct) =>
            {
                var result = await mediator.Send(new GetAllAirlinesQuery(paging), ct);
                var dtos = result.Items.Select(a => new AirlineAdminDto(a.Id, a.Iata, a.Icao, a.Name, a.CountryCode, a.AllianceCode)).ToList();
                return Results.Ok(new PagedResult<AirlineAdminDto>(dtos, result.Total, result.Page, result.PageSize));
            })
            .WithName("AdminGetAllAirlines")
            .WithSummary("List all airlines")
            .Produces<PagedResult<AirlineAdminDto>>(200)
            .ProducesValidationProblem();

            // GET /admin/airlines/{id}
            group.MapGet("{id:int}", async (int id, IMediator mediator, CancellationToken ct) =>
            {
                var a = await mediator.Send(new GetAirlineByIdQuery(id), ct);
                if (a is null) return Results.NotFound();
                return Results.Ok(new AirlineAdminDto(a.Id, a.Iata, a.Icao, a.Name, a.CountryCode, a.AllianceCode));
            })
            .WithName("AdminGetAirlineById")
            .WithSummary("Get airline by ID")
            .Produces<AirlineAdminDto>(200)
            .ProducesProblem(404);

            // POST /admin/airlines
            group.MapPost("", async ([FromBody] CreateAirlineRequest req, IMediator mediator, CancellationToken ct) =>
            {
                var id = await mediator.Send(new CreateAirlineCommand(req.Iata, req.Icao, req.Name, req.CountryCode, req.AllianceCode), ct);
                return Results.Created($"/admin/airlines/{id}", new { id });
            })
            .WithName("AdminCreateAirline")
            .WithSummary("Create airline")
            .WithDescription("409 when AllianceCode names an alliance that is not in the alliances table.")
            .Produces(201)
            .ProducesValidationProblem()
            .ProducesProblem(409);

            // PUT /admin/airlines/{id}
            group.MapPut("{id:int}", async (int id, [FromBody] UpdateAirlineRequest req, IMediator mediator, CancellationToken ct) =>
            {
                await mediator.Send(new UpdateAirlineCommand(id, req.Iata, req.Icao, req.Name, req.CountryCode, req.AllianceCode), ct);
                return Results.NoContent();
            })
            .WithName("AdminUpdateAirline")
            .WithSummary("Update airline")
            .WithDescription("409 when AllianceCode names an alliance that is not in the alliances table.")
            .Produces(204)
            .ProducesValidationProblem()
            .ProducesProblem(404)
            .ProducesProblem(409);

            // DELETE /admin/airlines/{id}
            group.MapDelete("{id:int}", async (int id, IMediator mediator, CancellationToken ct) =>
            {
                await mediator.Send(new DeleteAirlineCommand(id), ct);
                return Results.NoContent();
            })
            .WithName("AdminDeleteAirline")
            .WithSummary("Delete airline")
            .Produces(204)
            .ProducesValidationProblem()
            .ProducesProblem(409);
        }
    }
}
