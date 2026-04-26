using FlightAssistant.API.Authentication;
using FlightAssistant.Application.Commands.Flights;
using FlightAssistant.Application.Queries.Flights;
using FlightAssistant.Contracts.Requests.Flights;
using FlightAssistant.Contracts.Responses.Flights;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web.Resource;

namespace FlightAssistant.API.Endpoints
{
    public static class FlightEndpoints
    {
        private const string ScopeAccessAsUser = "access_as_user";

        public static void MapFlightEndpoints(this IEndpointRouteBuilder app)
        {
            var group = app.MapGroup("flights").RequireAuthorization().WithTags("Flights");

            // GET /flights — list all tracked flights for the authenticated traveler
            group.MapGet("", async (HttpContext http, IMediator mediator, CancellationToken ct) =>
            {
                http.VerifyUserHasAnyAcceptedScope(ScopeAccessAsUser);
                var travelerId = http.User.GetTravelerId();

                var results = await mediator.Send(new GetTravelerFlightsQuery(travelerId), ct);

                var dtos = results.Select(r => new FlightDto(
                    r.FlightId, r.AirlineIata, r.FlightNumber, r.ServiceDate, r.DepartureIata, r.ArrivalIata,
                    r.ScheduledDepartureUtc, r.ScheduledArrivalUtc,
                    r.EstimatedDepartureUtc, r.EstimatedArrivalUtc,
                    r.ActualDepartureUtc, r.ActualArrivalUtc,
                    null, r.Notify)).ToList();

                return Results.Ok(dtos);
            })
            .WithName("GetTrackedFlights")
            .WithSummary("List tracked flights")
            .WithDescription("Returns all flights the authenticated traveler is currently tracking, enriched with IATA codes and the latest known departure/arrival times.")
            .Produces<List<FlightDto>>(200);

            // POST /flights — track a flight
            group.MapPost("", async (HttpContext http, [FromBody] TrackFlightRequest req, IMediator mediator, CancellationToken ct) =>
            {
                http.VerifyUserHasAnyAcceptedScope(ScopeAccessAsUser);
                var travelerId = http.User.GetTravelerId();

                var flightId = await mediator.Send(new TrackFlightCommand(
                    travelerId,
                    req.AirlineIata,
                    req.FlightNumber,
                    req.ServiceDate,
                    req.DepartureAirportIata,
                    req.ArrivalAirportIata,
                    req.ScheduledDepartureUtc,
                    req.ScheduledArrivalUtc), ct);

                return Results.Created($"/flights/{flightId}", new { flightId });
            })
            .WithName("TrackFlight")
            .WithSummary("Track a flight")
            .WithDescription(
                "Adds a flight to the authenticated traveler's tracking list. " +
                "If the flight already exists in the system (same airline, number, and service date) it is reused; otherwise a new flight record is created. " +
                "Body fields: AirlineIata (e.g. 'BA'), FlightNumber (e.g. 'BA123'), ServiceDate (YYYY-MM-DD), " +
                "DepartureAirportIata, ArrivalAirportIata, ScheduledDepartureUtc, ScheduledArrivalUtc.")
            .Produces(201)
            .ProducesValidationProblem();

            // DELETE /flights/{flightId} — stop tracking a flight
            group.MapDelete("{flightId:int}", async (HttpContext http, int flightId, IMediator mediator, CancellationToken ct) =>
            {
                http.VerifyUserHasAnyAcceptedScope(ScopeAccessAsUser);
                var travelerId = http.User.GetTravelerId();
                await mediator.Send(new UntrackFlightCommand(travelerId, flightId), ct);
                return Results.NoContent();
            })
            .WithName("UntrackFlight")
            .WithSummary("Stop tracking a flight")
            .WithDescription(
                "Removes the specified flight from the authenticated traveler's tracking list. " +
                "Route parameter: flightId — the numeric ID returned when the flight was tracked.")
            .Produces(204)
            .ProducesProblem(404);

            // PATCH /flights/{flightId}/notify — toggle push notifications for a flight
            group.MapPatch("{flightId:int}/notify", async (HttpContext http, int flightId, [FromBody] bool notify, IMediator mediator, CancellationToken ct) =>
            {
                http.VerifyUserHasAnyAcceptedScope(ScopeAccessAsUser);
                var travelerId = http.User.GetTravelerId();
                await mediator.Send(new ToggleFlightNotificationCommand(travelerId, flightId, notify), ct);
                return Results.NoContent();
            })
            .WithName("ToggleFlightNotifications")
            .WithSummary("Toggle flight notifications")
            .WithDescription(
                "Enables or disables push notifications for a tracked flight. " +
                "Route parameter: flightId — the numeric flight ID. " +
                "Body: a JSON boolean (true to enable, false to disable). " +
                "Notifications are delivered via APNs to device tokens registered for the traveler.")
            .Produces(204)
            .ProducesProblem(404);

            // GET /flights/{flightId}/status — get flight status event history
            group.MapGet("{flightId:int}/status", async (HttpContext http, int flightId, IMediator mediator, CancellationToken ct) =>
            {
                http.VerifyUserHasAnyAcceptedScope(ScopeAccessAsUser);
                var events = await mediator.Send(new GetFlightStatusQuery(flightId), ct);
                var dtos = events.Select(e => new FlightStatusEventDto(e.Id, e.StatusCode, e.StatusName, e.EffectiveAtUtc, e.Source, e.Gate, e.Terminal, e.BaggageClaim, e.Remarks));
                return Results.Ok(dtos);
            })
            .WithName("GetFlightStatus")
            .WithSummary("Get flight status history")
            .WithDescription(
                "Returns the ordered list of status events recorded for the flight. " +
                "Each event captures the status code, gate/terminal info, baggage claim, and the time it became effective. " +
                "Route parameter: flightId — the numeric flight ID.")
            .Produces<IEnumerable<FlightStatusEventDto>>(200)
            .ProducesProblem(404);

            // POST /flights/{flightId}/sync — manually trigger a status refresh from external providers
            group.MapPost("{flightId:int}/sync", async (HttpContext http, int flightId, IMediator mediator, CancellationToken ct) =>
            {
                http.VerifyUserHasAnyAcceptedScope(ScopeAccessAsUser);
                var updated = await mediator.Send(new SyncFlightStatusCommand(flightId), ct);
                return Results.Ok(new { updated });
            })
            .WithName("SyncFlightStatus")
            .WithSummary("Manually sync flight status")
            .WithDescription(
                "Triggers an on-demand status refresh for the specified flight by querying the external flight data providers (AeroDataBox, with AviationStack as fallback). " +
                "Returns { updated: true } if new status data was fetched and persisted, or { updated: false } if the data was already up to date. " +
                "Route parameter: flightId — the numeric flight ID.")
            .Produces(200)
            .ProducesProblem(404);
        }

    }
}
