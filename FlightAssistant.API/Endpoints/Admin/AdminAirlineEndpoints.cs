using FlightAssistant.Application.Commands.Admin.Airlines;
using FlightAssistant.Application.Queries.Admin.Airlines;
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
            group.MapGet("", async (IMediator mediator, CancellationToken ct) =>
            {
                var results = await mediator.Send(new GetAllAirlinesQuery(), ct);
                return Results.Ok(results.Select(a => new AirlineAdminDto(a.Id, a.Iata, a.Icao, a.Name, a.CountryCode, a.AllianceCode)));
            })
            .WithName("AdminGetAllAirlines")
            .WithSummary("List all airlines")
            .Produces<IEnumerable<AirlineAdminDto>>(200);

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
            .Produces(201)
            .ProducesValidationProblem();

            // PUT /admin/airlines/{id}
            group.MapPut("{id:int}", async (int id, [FromBody] UpdateAirlineRequest req, IMediator mediator, CancellationToken ct) =>
            {
                await mediator.Send(new UpdateAirlineCommand(id, req.Iata, req.Icao, req.Name, req.CountryCode, req.AllianceCode), ct);
                return Results.NoContent();
            })
            .WithName("AdminUpdateAirline")
            .WithSummary("Update airline")
            .Produces(204)
            .ProducesProblem(404);

            // DELETE /admin/airlines/{id}
            group.MapDelete("{id:int}", async (int id, IMediator mediator, CancellationToken ct) =>
            {
                await mediator.Send(new DeleteAirlineCommand(id), ct);
                return Results.NoContent();
            })
            .WithName("AdminDeleteAirline")
            .WithSummary("Delete airline")
            .Produces(204)
            .ProducesProblem(409);
        }
    }
}
