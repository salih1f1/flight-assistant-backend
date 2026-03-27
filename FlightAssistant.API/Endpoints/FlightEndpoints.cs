using FlightAssistant.Application.Commands.Flights;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Application.Queries.Flights;
using FlightAssistant.Contracts.Requests.Flights;
using FlightAssistant.Contracts.Responses.Flights;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web.Resource;
using System.Security.Claims;

namespace FlightAssistant.API.Endpoints
{
    public static class FlightEndpoints
    {
        private const string ScopeAccessAsUser = "access_as_user";

        public static void MapFlightEndpoints(this IEndpointRouteBuilder app)
        {
            var group = app.MapGroup("flights").RequireAuthorization();

            // GET /flights — list all tracked flights for the authenticated traveler
            group.MapGet("", async (HttpContext http, IMediator mediator, IRefDataRepository refData, CancellationToken ct) =>
            {
                http.VerifyUserHasAnyAcceptedScope(ScopeAccessAsUser);
                var travelerId = GetTravelerId(http);

                var results = await mediator.Send(new GetTravelerFlightsQuery(travelerId), ct);

                var dtos = new List<FlightDto>();
                foreach (var r in results)
                {
                    var airlineIata = await refData.GetAirlineIataByIdAsync(r.AirlineId, ct) ?? r.AirlineId.ToString();
                    var depIata = await refData.GetAirportIataByIdAsync(r.DepartureAirportId, ct) ?? r.DepartureAirportId.ToString();
                    var arrIata = await refData.GetAirportIataByIdAsync(r.ArrivalAirportId, ct) ?? r.ArrivalAirportId.ToString();
                    dtos.Add(new FlightDto(r.FlightId, airlineIata, r.FlightNumber, r.ServiceDate, depIata, arrIata,
                        r.ScheduledDepartureUtc, r.ScheduledArrivalUtc,
                        r.EstimatedDepartureUtc, r.EstimatedArrivalUtc,
                        r.ActualDepartureUtc, r.ActualArrivalUtc,
                        null, r.Notify));
                }

                return Results.Ok(dtos);
            });

            // POST /flights — track a flight
            group.MapPost("", async (HttpContext http, [FromBody] TrackFlightRequest req, IMediator mediator, CancellationToken ct) =>
            {
                http.VerifyUserHasAnyAcceptedScope(ScopeAccessAsUser);
                var travelerId = GetTravelerId(http);

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
            });

            // DELETE /flights/{flightId} — stop tracking a flight
            group.MapDelete("{flightId:int}", async (HttpContext http, int flightId, IMediator mediator, CancellationToken ct) =>
            {
                http.VerifyUserHasAnyAcceptedScope(ScopeAccessAsUser);
                var travelerId = GetTravelerId(http);
                await mediator.Send(new UntrackFlightCommand(travelerId, flightId), ct);
                return Results.NoContent();
            });

            // PATCH /flights/{flightId}/notify — toggle notifications
            group.MapPatch("{flightId:int}/notify", async (HttpContext http, int flightId, [FromBody] bool notify, IMediator mediator, CancellationToken ct) =>
            {
                http.VerifyUserHasAnyAcceptedScope(ScopeAccessAsUser);
                var travelerId = GetTravelerId(http);
                await mediator.Send(new ToggleFlightNotificationCommand(travelerId, flightId, notify), ct);
                return Results.NoContent();
            });

            // GET /flights/{flightId}/status — get flight status history
            group.MapGet("{flightId:int}/status", async (HttpContext http, int flightId, IMediator mediator, CancellationToken ct) =>
            {
                http.VerifyUserHasAnyAcceptedScope(ScopeAccessAsUser);
                var events = await mediator.Send(new GetFlightStatusQuery(flightId), ct);
                var dtos = events.Select(e => new FlightStatusEventDto(e.Id, e.StatusCode, e.StatusName, e.EffectiveAtUtc, e.Source, e.Gate, e.Terminal, e.BaggageClaim, e.Remarks));
                return Results.Ok(dtos);
            });

            // POST /flights/{flightId}/sync — manually trigger status sync
            group.MapPost("{flightId:int}/sync", async (HttpContext http, int flightId, IMediator mediator, CancellationToken ct) =>
            {
                http.VerifyUserHasAnyAcceptedScope(ScopeAccessAsUser);
                var updated = await mediator.Send(new SyncFlightStatusCommand(flightId), ct);
                return Results.Ok(new { updated });
            });
        }

        private static Guid GetTravelerId(HttpContext http)
        {
            var oidClaim = http.User.FindFirstValue("oid") ?? http.User.FindFirstValue("sub")
                ?? throw new InvalidOperationException("User OID claim missing.");
            return Guid.Parse(oidClaim);
        }
    }
}
