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
                "- 503: the flight data provider is throttling requests, or this month's flight data budget is nearly used up, so only searches already cached are answered; retry after the period in the Retry-After header.\n" +
                "- 429: too many searches from this caller (per signed-in traveler, or per client IP when anonymous); retry after the period in the Retry-After header.\n")
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
                    null, r.Notify, r.CountsTowardCap)).ToList();

                return Results.Ok(new TrackedFlightsDto(
                    dtos, result.Total, result.Page, result.PageSize,
                    result.ActiveTrackedFlights, result.TrackingCap));
            })
            .WithName("GetTrackedFlights")
            .WithSummary("List tracked flights")
            .WithDescription(
                "Returns a paginated list of flights the authenticated traveler is tracking. Supports ?page and ?pageSize (default 50, max 200). " +
                "countsTowardCap is true while the status sync still polls the flight, which is what occupies a slot against the tracking cap " +
                "POST /flights enforces; it turns false once the flight has landed or been cancelled for long enough, or its arrival time passed " +
                "a day ago without a landing being reported. " +
                "activeTrackedFlights counts every flight of this traveler that still occupies a slot, across all pages, and trackingCap is the configured limit — " +
                "so remaining capacity is trackingCap minus activeTrackedFlights, floored at zero, without paging through the list. " +
                "A traveler grandfathered above the cap, or one whose cap was later lowered in configuration, reports more active flights than the cap. " +
                "Both numbers are as of this response: tracking can still be refused if capacity is used in the meantime. " +
                "total is a different number — it counts every flight the traveler tracks, including ones that no longer occupy a slot. " +
                "Tracking a flight the traveler already tracks still succeeds at the cap.")
            .Produces<TrackedFlightsDto>(200)
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
                "DepartureAirportIata, ArrivalAirportIata, ScheduledDepartureUtc, ScheduledArrivalUtc. " +
                "A traveler can track a limited number of flights at a time. GET /flights reports the limit in force as trackingCap, " +
                "and how many slots are in use as activeTrackedFlights (#397). " +
                "A flight counts until its status sync stops: by default 30 minutes after it lands or is cancelled, or a day after its arrival time if no landing is reported. " +
                "Tracking another returns 409 and adds nothing; tracking a flight the traveler already tracks still returns 201. " +
                "Rate-limited per traveler: too many tracking requests return 429 with a Retry-After header. " +
                "Returns 404 if the airline or either airport in the request is not known to the system. ")
            .Produces(201)
            .ProducesValidationProblem()
            .ProducesProblem(404)
            .ProducesProblem(409)
            .RequireRateLimiting("flight-track");

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
                "Route parameter: flightId — the numeric ID returned when the flight was tracked. " +
                "Idempotent: untracking a flight the traveler does not track also returns 204, so there is no 404. ")
            .Produces(204);

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
                "Returns the flight's full status-event history — every event, not a page — most recently recorded first. " +
                "The order is the order events were recorded (by event id), not effectiveAtUtc, which can move backwards when an estimate " +
                "returns to an earlier value; so the first row is the flight's current status. " +
                "Each event captures the status code, gate/terminal info, baggage claim, and the time it became effective. " +
                "Route parameter: flightId — the numeric flight ID. " +
                "Returns 403 if the authenticated traveler does not track this flight — including when no flight with that id exists, " +
                "so the response does not reveal whether a flight id is real to someone who does not track it. There is no 404. ")
            .Produces<IEnumerable<FlightStatusEventDto>>(200)
            .ProducesProblem(403);

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
                "A flight synced within the manual-sync cooldown (5 minutes by default) makes no call either. " +
                "Once this month's flight data budget is used up, only a flight in its near-departure window (by default, from 4 hours before departure until 30 minutes after it lands) is refreshed; any other returns 503. " +
                "Returns { updated: true } when an event was recorded — a new status, a revised time for the status the flight already has, " +
                "or a gate, terminal or baggage-claim change on the current status — and { updated: false } when none was: no call was made, " +
                "or the response repeated what was already recorded. { updated: false } still does not mean nothing changed: a revised " +
                "estimated arrival updates the flight and is visible immediately on GET /flights. " +
                "Rate-limited per traveler: too many sync requests return 429 with a Retry-After header. " +
                "Route parameter: flightId — the numeric flight ID. " +
                "Returns 403 if the authenticated traveler does not track the flight, and 404 if no flight with that id exists. ")
            .Produces(200)
            .ProducesProblem(403)
            .ProducesProblem(404)
            .ProducesProblem(503)
            .RequireRateLimiting("flight-sync");
        }

    }
}
