using FlightAssistant.Application.Queries.Lounges;
using FlightAssistant.Contracts.Responses.Lounges;
using MediatR;
using Microsoft.Identity.Web.Resource;
using System.Security.Claims;

namespace FlightAssistant.API.Endpoints
{
    public static class LoungeEndpoints
    {
        private const string ScopeAccessAsUser = "access_as_user";

        public static void MapLoungeEndpoints(this IEndpointRouteBuilder app)
        {
            var group = app.MapGroup("lounges").RequireAuthorization();

            // GET /lounges?airportIata=LHR — list lounges at an airport, annotated with eligibility
            group.MapGet("", async (HttpContext http, string airportIata, IMediator mediator, CancellationToken ct) =>
            {
                http.VerifyUserHasAnyAcceptedScope(ScopeAccessAsUser);
                var travelerId = TryGetTravelerId(http);

                var results = await mediator.Send(new GetLoungesAtAirportQuery(airportIata, travelerId), ct);
                var dtos = results.Select(l => new LoungeDto(
                    l.Id, l.Name, l.Terminal, l.LocationHint, l.Hours,
                    l.IsAirside, l.IsDomesticOnly, l.IsInternationalOnly,
                    l.Amenities, l.IsEligible, l.EligibleVia));

                return Results.Ok(dtos);
            });
        }

        private static Guid? TryGetTravelerId(HttpContext http)
        {
            var claim = http.User.FindFirstValue("oid") ?? http.User.FindFirstValue("sub");
            return Guid.TryParse(claim, out var id) ? id : null;
        }
    }
}
