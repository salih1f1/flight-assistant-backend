using FlightAssistant.Application.Commands.Admin.Flights;
using FlightAssistant.Application.Commands.Flights;
using FlightAssistant.Application.Queries.Admin.Flights;
using FlightAssistant.Contracts.Paging;
using FlightAssistant.Contracts.Responses.Admin;
using MediatR;

namespace FlightAssistant.API.Endpoints.Admin
{
    public static class AdminFlightEndpoints
    {
        public static void MapAdminFlightEndpoints(this IEndpointRouteBuilder admin)
        {
            var group = admin.MapGroup("flights")
                .WithTags("Admin - Flights");

            // GET /admin/flights
            group.MapGet("", async ([AsParameters] PagedRequest paging, IMediator mediator, CancellationToken ct) =>
            {
                var result = await mediator.Send(new GetAllFlightsAdminQuery(paging), ct);
                var dtos = result.Items.Select(f => new AdminFlightDto(
                    f.Id, f.AirlineId, f.FlightNumber, f.DepartureAirportId, f.ArrivalAirportId,
                    f.ServiceDate, f.ScheduledDepartureUtc, f.ScheduledArrivalUtc,
                    f.EstimatedDepartureUtc, f.EstimatedArrivalUtc,
                    f.ActualDepartureUtc, f.ActualArrivalUtc,
                    f.EquipmentAircraftIcao, f.ExternalId)).ToList();
                return Results.Ok(new PagedResult<AdminFlightDto>(dtos, result.Total, result.Page, result.PageSize));
            })
            .WithName("AdminGetAllFlights")
            .WithSummary("List all flights")
            .Produces<PagedResult<AdminFlightDto>>(200)
            .ProducesValidationProblem();

            // GET /admin/flights/{id}
            group.MapGet("{id:int}", async (int id, IMediator mediator, CancellationToken ct) =>
            {
                var f = await mediator.Send(new GetAdminFlightByIdQuery(id), ct);
                if (f is null) return Results.NotFound();
                return Results.Ok(new AdminFlightDto(
                    f.Id, f.AirlineId, f.FlightNumber, f.DepartureAirportId, f.ArrivalAirportId,
                    f.ServiceDate, f.ScheduledDepartureUtc, f.ScheduledArrivalUtc,
                    f.EstimatedDepartureUtc, f.EstimatedArrivalUtc,
                    f.ActualDepartureUtc, f.ActualArrivalUtc,
                    f.EquipmentAircraftIcao, f.ExternalId));
            })
            .WithName("AdminGetFlightById")
            .WithSummary("Get flight by ID")
            .Produces<AdminFlightDto>(200)
            .ProducesProblem(404);

            // DELETE /admin/flights/{id}
            group.MapDelete("{id:int}", async (int id, IMediator mediator, CancellationToken ct) =>
            {
                await mediator.Send(new DeleteFlightAdminCommand(id), ct);
                return Results.NoContent();
            })
            .WithName("AdminDeleteFlight")
            .WithSummary("Delete flight")
            .Produces(204);

            // POST /admin/flights/{id}/sync
            group.MapPost("{id:int}/sync", async (int id, IMediator mediator, CancellationToken ct) =>
            {
                var updated = await mediator.Send(new SyncFlightStatusCommand(null, id), ct);
                return Results.Ok(new { updated });
            })
            .WithName("AdminSyncFlightStatus")
            .WithSummary("Sync flight status")
            .Produces(200);
        }
    }
}
