using FlightAssistant.Application.Queries.Visa;
using FlightAssistant.Contracts.Responses.Visa;
using MediatR;
using Microsoft.Identity.Web.Resource;

namespace FlightAssistant.API.Endpoints
{
    public static class VisaEndpoints
    {
        private const string ScopeAccessAsUser = "access_as_user";

        public static void MapVisaEndpoints(this IEndpointRouteBuilder app)
        {
            var group = app.MapGroup("visa").RequireAuthorization();

            // GET /visa?passportCountry=GB&destinationCountry=JP
            group.MapGet("", async (HttpContext http, string passportCountry, string destinationCountry, IMediator mediator, CancellationToken ct) =>
            {
                http.VerifyUserHasAnyAcceptedScope(ScopeAccessAsUser);
                var result = await mediator.Send(new GetVisaRequirementsQuery(passportCountry, destinationCountry), ct);
                if (result is null) return Results.NotFound();
                return Results.Ok(new VisaRequirementDto(result.PassportCountryCode, result.DestinationCountryCode, result.AdmissionType, result.StayDays, result.Notes));
            });
        }
    }
}
