using FlightAssistant.API.Authentication;
using FlightAssistant.Application.Commands.Flights;
using FlightAssistant.Application.Queries.Flights;
using FlightAssistant.Application.Validators;
using FlightAssistant.Contracts.Paging;
using FlightAssistant.Contracts.Requests.Flights;
using FlightAssistant.Contracts.Responses.Flights;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web.Resource;
using System.Collections.Generic;

namespace FlightAssistant.API.Endpoints
{
    public static class FlightEndpoints
    {
        private const string ScopeAccessAsUser = "access_as_user";

        public static void MapFlightEndpoints(this IEndpointRouteBuilder app)
        {
            var group = app.MapGroup("flights").RequireAuthorization().WithTags("Flights");

            // GET /flights/search?date=2025-06-01&from=LHR&to=JFK
            // GET /flights/search?date=2025-06-01&flightNumber=BA123
            group.MapGet("search", async (DateOnly date, IMediator mediator, CancellationToken ct, string? from = null, string? to = null, string? flightNumber = null, bool includeCodeshares = false) =>
            {
                var results = await mediator.Send(new SearchFlightsQuery(from, to, date, flightNumber, includeCodeshares), ct);
                var dtos = results.Select(r => new FlightSearchResultDto(
                    r.FlightNumber, r.ServiceDate, r.DepartureAirportIata, r.ArrivalAirportIata,
                    r.ScheduledDepartureUtc, r.ScheduledArrivalUtc)).ToList();
                return Results.Ok(dtos);
            })
            .WithName("SearchFlights")
            .WithSummary("Search flights by route or flight number on a date")
            .WithDescription(
                "Searches scheduled flights to populate the 'add flight' picker. Each returned row has exactly " +
                "the same shape as TrackFlightRequest (flightNumber, serviceDate, departureAirportIata, " +
                "arrivalAirportIata, scheduledDepartureUtc, scheduledArrivalUtc), so a result can be posted " +
                "straight to POST /flights, unmodified, to track it.\n\n" +
                "Search criteria (query params):\n" +
                "- date (required): the departure date in YYYY-MM-DD format. Must fall within the provider's rolling past/future horizon.\n" +
                "- from / to (optional): departure and arrival airport as 3-letter IATA codes (e.g. LHR, JFK). Case-insensitive. Must be supplied together.\n" +
                // Rendered from FlightNumberFormat so the documented rule cannot drift from the
                // regex the validator actually applies — which is exactly what happened before.
                "- flightNumber (optional): an IATA flight code — " + FlightNumberFormat.Description +
                ". Case-insensitive. The same rule POST /flights applies, so any flightNumber this endpoint accepts is trackable.\n" +
                "- includeCodeshares (optional, default false): when true, includes codeshare-marketed rows for the same physical departure alongside the operating flight's own row; when false, only the operating flight's own row is returned.\n\n" +
                "You must provide either a full route (both from and to) OR a flightNumber, in addition to date; otherwise the request is rejected with 400.\n\n" +
                "Each result is one flight designator — the same aircraft sold under several marketing numbers " +
                "(e.g. TK1028 is also 6E4120, MS9668, RJ3710 and SQ6228) appears as one row per number, not " +
                "grouped. There is no separate 'operating carrier' field: the airline is always the first two " +
                "characters of the result's own flightNumber.\n\n" +
                "Responses:\n" +
                "- 200: list of matching flights (empty list when the provider responds successfully but nothing matches).\n" +
                "- 400: invalid/insufficient parameters.\n" +
                "- 500: server-side configuration issue with the flight data provider.\n" +
                "- 502: the flight data provider was unreachable or returned an unexpected error.\n" +
                "- 503: the flight data provider is throttling requests; retry after the period in the Retry-After header.")
            .Produces<List<FlightSearchResultDto>>(200)
            .ProducesValidationProblem()
            .ProducesProblem(500)
            .ProducesProblem(502)
            .ProducesProblem(503)
            .AllowAnonymous() // search is public — no auth needed to browse schedules
            .RequireRateLimiting("flight-search");

            // GET /flights — paginated list of tracked flights for the authenticated traveler
            group.MapGet("", async (HttpContext http, [AsParameters] PagedRequest paging, IMediator mediator, CancellationToken ct) =>
            {
                http.VerifyUserHasAnyAcceptedScope(ScopeAccessAsUser);
                var travelerId = http.User.GetTravelerId();

                var result = await mediator.Send(new GetTravelerFlightsQuery(travelerId, paging), ct);

                var dtos = result.Items.Select(r => new FlightDto(
                    r.FlightId, r.AirlineIata, r.FlightNumber, r.ServiceDate, r.DepartureIata, r.ArrivalIata,
                    r.ScheduledDepartureUtc, r.ScheduledArrivalUtc,
                    r.EstimatedDepartureUtc, r.EstimatedArrivalUtc,
                    r.ActualDepartureUtc, r.ActualArrivalUtc,
                    null, r.Notify)).ToList();

                return Results.Ok(new PagedResult<FlightDto>(dtos, result.Total, result.Page, result.PageSize));
            })
            .WithName("GetTrackedFlights")
            .WithSummary("List tracked flights")
            .WithDescription("Returns a paginated list of flights the authenticated traveler is tracking. Supports ?page and ?pageSize (default 50, max 200).")
            .Produces<PagedResult<FlightDto>>(200)
            .ProducesValidationProblem();

            // POST /flights — track a flight
            group.MapPost("", async (HttpContext http, [FromBody] TrackFlightRequest req, IMediator mediator, CancellationToken ct) =>
            {
                http.VerifyUserHasAnyAcceptedScope(ScopeAccessAsUser);
                var travelerId = http.User.GetTravelerId();

                var flightId = await mediator.Send(new TrackFlightCommand(
                    travelerId,
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
                "Adds a flight to the authenticated traveler's tracking list. A row returned by GET /flights/search " +
                "can be posted here unmodified — this body is exactly that shape. " +
                "If the flight already exists in the system (same airline, number, and service date) it is reused; otherwise a new flight record is created. " +
                "The airline is derived from the first two characters of FlightNumber (e.g. 'BA123' -> 'BA', '6E4120' -> '6E'); " +
                "there is no separate airline field. " +
                "Body fields: FlightNumber (e.g. 'BA123' or '6E4120'), ServiceDate (YYYY-MM-DD), " +
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
            group.MapPatch("{flightId:int}/notify", async (HttpContext http, int flightId, [FromBody] ToggleNotifyRequest request, IMediator mediator, CancellationToken ct) =>
            {
                http.VerifyUserHasAnyAcceptedScope(ScopeAccessAsUser);
                var travelerId = http.User.GetTravelerId();
                await mediator.Send(new ToggleFlightNotificationCommand(travelerId, flightId, request.Notify), ct);
                return Results.NoContent();
            })
            .WithName("ToggleFlightNotifications")
            .WithSummary("Toggle flight notifications")
            .WithDescription(
                "Enables or disables push notifications for a tracked flight. " +
                "Route parameter: flightId — the numeric flight ID. " +
                "Body: { \"notify\": true } (true to enable, false to disable). " +
                "Notifications are delivered via APNs to device tokens registered for the traveler.")
            .Produces(204)
            .ProducesProblem(404);

            // GET /flights/{flightId}/status — get flight status event history
            group.MapGet("{flightId:int}/status", async (HttpContext http, int flightId, IMediator mediator, CancellationToken ct) =>
            {
                http.VerifyUserHasAnyAcceptedScope(ScopeAccessAsUser);
                var travelerId = http.User.GetTravelerId();
                var events = await mediator.Send(new GetFlightStatusQuery(travelerId, flightId), ct);
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

            // POST /flights/{flightId}/sync — manually trigger a status refresh; the flight's sync mode decides whether the provider is called
            group.MapPost("{flightId:int}/sync", async (HttpContext http, int flightId, IMediator mediator, CancellationToken ct) =>
            {
                http.VerifyUserHasAnyAcceptedScope(ScopeAccessAsUser);
                var travelerId = http.User.GetTravelerId();
                var updated = await mediator.Send(new SyncFlightStatusCommand(travelerId, flightId, SyncTrigger.Traveler), ct);
                return Results.Ok(new { updated });
            })
            .WithName("SyncFlightStatus")
            .WithSummary("Manually sync flight status")
            .WithDescription(
                "Triggers an on-demand status refresh for the specified flight. Whether the external flight data provider is queried depends on the flight's sync mode: " +
                "a flight not yet inside the provider's live-status window makes no provider call. " +
                "Returns { updated: true } if a new status was recorded, or { updated: false } if nothing new was found or no call was made. " +
                "Route parameter: flightId — the numeric flight ID.")
            .Produces(200)
            .ProducesProblem(404);
        }

    }
}
