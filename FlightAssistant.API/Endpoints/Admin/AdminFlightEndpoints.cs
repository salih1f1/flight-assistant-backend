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

            // GET /admin/flights/{id}/detail — the flight with its current status and recent
            // status-event history, newest first.
            group.MapGet("{id:int}/detail", async (int id, IMediator mediator, CancellationToken ct) =>
            {
                var result = await mediator.Send(new GetAdminFlightDetailQuery(id), ct);
                if (result is null) return Results.NotFound();
                return Results.Ok(new AdminFlightDetailDto(
                    result.Id, result.AirlineId, result.FlightNumber, result.DepartureAirportId, result.ArrivalAirportId,
                    result.ServiceDate, result.ScheduledDepartureUtc, result.ScheduledArrivalUtc,
                    result.EstimatedDepartureUtc, result.EstimatedArrivalUtc,
                    result.ActualDepartureUtc, result.ActualArrivalUtc,
                    result.EquipmentAircraftIcao, result.ExternalId,
                    result.CurrentStatus is { } current ? ToStatusEventDto(current) : null,
                    result.StatusHistory.Select(ToStatusEventDto).ToList(),
                    result.StatusHistoryTruncated));
            })
            .WithName("AdminGetFlightDetail")
            .WithSummary("Get flight detail")
            .WithDescription(
                $"Returns the flight with its current status and its {GetAdminFlightDetailQueryHandler.MaxStatusHistory} " +
                "most recent status events, newest first — the record of what the sync actually saw. " +
                "Not the full history: a long-tracked flight polled every couple of minutes accumulates " +
                "status-event rows without bound. StatusHistoryTruncated is true when the cap actually " +
                "cut rows off; when it is, see GET /admin/data/flight-status-events for the rest.")
            .Produces<AdminFlightDetailDto>(200)
            .ProducesProblem(404);

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

        private static AdminFlightStatusEventDto ToStatusEventDto(AdminFlightStatusEventResult e) => new(
            e.Id, e.StatusCode, e.StatusName, e.ProviderRawStatus, e.EffectiveAtUtc, e.RecordedAtUtc, e.Source);
    }
}
