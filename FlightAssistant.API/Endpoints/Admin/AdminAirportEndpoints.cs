using FlightAssistant.Application.Commands.Admin.Airports;
using FlightAssistant.Application.Queries.Admin.Airports;
using FlightAssistant.Contracts.Paging;
using FlightAssistant.Contracts.Requests.Admin.Airports;
using FlightAssistant.Contracts.Responses.Admin;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace FlightAssistant.API.Endpoints.Admin
{
    public static class AdminAirportEndpoints
    {
        public static void MapAdminAirportEndpoints(this IEndpointRouteBuilder admin)
        {
            var group = admin.MapGroup("airports")
                .WithTags("Admin - Airports");

            // GET /admin/airports
            group.MapGet("", async ([AsParameters] PagedRequest paging, IMediator mediator, CancellationToken ct) =>
            {
                var result = await mediator.Send(new GetAllAirportsQuery(paging), ct);
                var dtos = result.Items.Select(a => new AirportAdminDto(
                    a.Id, a.Iata, a.Icao, a.Name, a.City, a.CountryCode,
                    a.Latitude, a.Longitude, a.ElevationFt)).ToList();
                return Results.Ok(new PagedResult<AirportAdminDto>(dtos, result.Total, result.Page, result.PageSize));
            })
            .WithName("AdminGetAllAirports")
            .WithSummary("List all airports")
            .Produces<PagedResult<AirportAdminDto>>(200)
            .ProducesValidationProblem();

            // GET /admin/airports/{id}
            group.MapGet("{id:int}", async (int id, IMediator mediator, CancellationToken ct) =>
            {
                var a = await mediator.Send(new GetAirportByIdQuery(id), ct);
                if (a is null) return Results.NotFound();
                return Results.Ok(new AirportAdminDto(a.Id, a.Iata, a.Icao, a.Name, a.City, a.CountryCode,
                    a.Latitude, a.Longitude, a.ElevationFt));
            })
            .WithName("AdminGetAirportById")
            .WithSummary("Get airport by ID")
            .Produces<AirportAdminDto>(200)
            .ProducesProblem(404);

            // POST /admin/airports
            group.MapPost("", async ([FromBody] CreateAirportRequest req, IMediator mediator, CancellationToken ct) =>
            {
                var id = await mediator.Send(new CreateAirportCommand(
                    req.Iata, req.Icao, req.Name, req.City, req.CountryCode,
                    req.Latitude, req.Longitude, req.ElevationFt), ct);
                return Results.Created($"/admin/airports/{id}", new { id });
            })
            .WithName("AdminCreateAirport")
            .WithSummary("Create airport")
            .Produces(201)
            .ProducesValidationProblem();

            // PUT /admin/airports/{id}
            group.MapPut("{id:int}", async (int id, [FromBody] UpdateAirportRequest req, IMediator mediator, CancellationToken ct) =>
            {
                await mediator.Send(new UpdateAirportCommand(id, req.Iata, req.Icao, req.Name, req.City, req.CountryCode,
                    req.Latitude, req.Longitude, req.ElevationFt), ct);
                return Results.NoContent();
            })
            .WithName("AdminUpdateAirport")
            .WithSummary("Update airport")
            .Produces(204)
            .ProducesProblem(404);

            // DELETE /admin/airports/{id}
            group.MapDelete("{id:int}", async (int id, IMediator mediator, CancellationToken ct) =>
            {
                await mediator.Send(new DeleteAirportCommand(id), ct);
                return Results.NoContent();
            })
            .WithName("AdminDeleteAirport")
            .WithSummary("Delete airport")
            .Produces(204);
        }
    }
}
