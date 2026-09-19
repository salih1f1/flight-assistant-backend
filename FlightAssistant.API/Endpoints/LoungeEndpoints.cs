using FlightAssistant.API.Authentication;
using FlightAssistant.Application.Queries.Lounges;
using FlightAssistant.Contracts.Responses.Lounges;
using MediatR;

namespace FlightAssistant.API.Endpoints
{
    public static class LoungeEndpoints
    {
        public static void MapLoungeEndpoints(this IEndpointRouteBuilder app)
        {
            var group = app.MapGroup("lounges").RequireAuthorization("AccessAsUser").WithTags("Lounges");

            // GET /lounges?airportIata=LHR — list lounges at an airport, annotated with eligibility
            group.MapGet("", async (HttpContext http, string airportIata, IMediator mediator, CancellationToken ct) =>
            {
                var travelerId = (Guid?)http.User.GetTravelerId();

                var results = await mediator.Send(new GetLoungesAtAirportQuery(airportIata, travelerId), ct);
                var dtos = results.Select(l => new LoungeDto(
                    l.Id, l.Name, l.Terminal, l.LocationHint, l.Hours,
                    l.IsAirside, l.IsDomesticOnly, l.IsInternationalOnly,
                    l.Amenities, l.IsEligible, l.EligibleVia));

                return Results.Ok(dtos);
            })
            .WithName("GetLoungesAtAirport")
            .WithSummary("List lounges at an airport")
            .WithDescription(
                "Returns all lounges at the specified airport, each annotated with whether the authenticated traveler is eligible to access it. " +
                "Eligibility is computed from the traveler's loyalty memberships and tier benefits. " +
                "Query parameter: airportIata — the IATA airport code (e.g. 'LHR', 'JFK'). " +
                "An airport with no lounges is an empty list, not a 404 — this route never reports one. " +
                "Returns 400 if airportIata is missing or is not a 3-letter IATA code. ")
            .Produces<IEnumerable<LoungeDto>>(200)
            .ProducesValidationProblem();
        }

    }
}
