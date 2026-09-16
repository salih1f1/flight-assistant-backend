using FlightAssistant.Application.Commands.Admin.Flights;
using FlightAssistant.Application.Commands.Flights;
using FlightAssistant.Application.Queries.Admin.Flights;
using FlightAssistant.Application.Queries.Flights;
using FlightAssistant.Contracts.Paging;
using FlightAssistant.Contracts.Responses.Admin;
using FlightAssistant.Contracts.Responses.Flights;
using MediatR;

namespace FlightAssistant.API.Endpoints.Admin
{
    public static class AdminFlightEndpoints
    {
        public static void MapAdminFlightEndpoints(this IEndpointRouteBuilder admin)
        {
            var group = admin.MapGroup("flights")
                .WithTags("Admin - Flights");

            // GET /admin/flights/search — GET /flights/search for the admin panel (#340), which
            // can't call the public route: it has no AdminPanel CORS policy.
            group.MapGet("search", async (DateOnly date, IMediator mediator, CancellationToken ct, string? from = null, string? to = null, string? flightNumber = null, bool includeCodeshares = false) =>
            {
                var results = await mediator.Send(new SearchFlightsQuery(from, to, date, flightNumber, includeCodeshares), ct);
                return Results.Ok(results.Select(r => new FlightSearchResultDto(
                    r.FlightNumber, r.ServiceDate, r.DepartureAirportIata, r.ArrivalAirportIata,
                    r.ScheduledDepartureUtc, r.ScheduledArrivalUtc)).ToList());
            })
            .WithName("AdminSearchFlights")
            .WithSummary("Search flights by route or flight number on a date")
            .WithDescription(
                "The same search, inputs and responses as GET /flights/search, for the admin panel. " +
                "Each row can be posted unmodified to POST /admin/travelers/{id}/flights. Every distinct " +
                "search that misses the cache is a paid AeroAPI call, so it shares the flight-search rate limit, counted per admin. " +
                "Once this month's flight data budget is nearly used up, a search that isn't cached returns 503.")
            .Produces<List<FlightSearchResultDto>>(200)
            .ProducesValidationProblem()
            .ProducesProblem(500)
            .ProducesProblem(502)
            .ProducesProblem(503)
            .RequireRateLimiting("flight-search");

            // GET /admin/flights/{id}/detail — the flight with its current status and recent
            // status-event history, most recently recorded first (by id, #351 — not by effective
            // time, which can move backwards when an estimate returns to an earlier value).
            group.MapGet("{id:int}/detail", async (int id, IMediator mediator, CancellationToken ct) =>
            {
                var result = await mediator.Send(new GetAdminFlightDetailQuery(id), ct);
                if (result is null) return Results.NotFound();
                return Results.Ok(new AdminFlightDetailDto(
                    result.Id, result.AirlineId, result.FlightNumber, result.DepartureAirportId, result.ArrivalAirportId,
                    result.ServiceDate, result.ScheduledDepartureUtc, result.ScheduledArrivalUtc,
                    result.EstimatedDepartureUtc, result.EstimatedArrivalUtc,
                    result.ActualDepartureUtc, result.ActualArrivalUtc,
                    result.EquipmentAircraftIcao,
                    result.CurrentStatus is { } current ? ToStatusEventDto(current) : null,
                    result.StatusHistory.Select(ToStatusEventDto).ToList(),
                    result.StatusHistoryTruncated));
            })
            .WithName("AdminGetFlightDetail")
            .WithSummary("Get flight detail")
            .WithDescription(
                $"Returns the flight with its current status and its {GetAdminFlightDetailQueryHandler.MaxStatusHistory} " +
                "most recent status events, most recently recorded first — the record of what the sync actually saw, in the order it saw it. " +
                "Not ordered by effectiveAtUtc, which can move backwards when an estimate returns to an earlier value. " +
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
                    f.EquipmentAircraftIcao)).ToList();
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
                    f.EquipmentAircraftIcao));
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
                var updated = await mediator.Send(new SyncFlightStatusCommand(null, id, SyncTrigger.Admin), ct);
                return Results.Ok(new { updated });
            })
            .WithName("AdminSyncFlightStatus")
            .WithSummary("Sync flight status")
            .WithDescription(
                "Syncs the flight's status now. A flight not yet inside the provider's live-status window makes no provider call, " +
                "and a flight no traveler tracks makes no call and has its sync plan cleared; both return { updated: false }. " +
                "Otherwise { updated: true } means an event was recorded — a new status, a revised time for the status the flight already has, " +
                "or a gate, terminal or baggage-claim change on the current status — and { updated: false } means none was, which still allows " +
                "a revised estimated arrival or aircraft type to have been saved. " +
                "Once this month's flight data budget is used up, only a flight in its near-departure window (by default, from 4 hours before departure until 30 minutes after it lands) makes a call; any other returns 503.")
            .Produces(200)
            .ProducesProblem(503);
        }

        private static AdminFlightStatusEventDto ToStatusEventDto(AdminFlightStatusEventResult e) => new(
            e.Id, e.StatusCode, e.StatusName, e.ProviderRawStatus, e.EffectiveAtUtc, e.RecordedAtUtc, e.Source);
    }
}
