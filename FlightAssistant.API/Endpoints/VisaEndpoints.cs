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
            var group = app.MapGroup("visa").RequireAuthorization().WithTags("Visa");

            // GET /visa?passportCountry=GB&destinationCountry=JP
            group.MapGet("", async (HttpContext http, string passportCountry, string destinationCountry, IMediator mediator, CancellationToken ct) =>
            {
                http.VerifyUserHasAnyAcceptedScope(ScopeAccessAsUser);
                var result = await mediator.Send(new GetVisaRequirementsQuery(passportCountry, destinationCountry), ct);
                return Results.Ok(new VisaRequirementDto(result.PassportCountryCode, result.DestinationCountryCode, result.AdmissionType, result.StayDays, result.Notes));
            })
            .WithName("GetVisaRequirements")
            .WithSummary("Get visa requirements")
            .WithDescription(
                "Returns the visa and entry requirements for the given passport/destination country pair. " +
                "Query parameters: " +
                "passportCountry — ISO 3166-1 alpha-2 country code of the traveler's passport (e.g. 'GB'); " +
                "destinationCountry — ISO 3166-1 alpha-2 country code of the destination (e.g. 'JP'). " +
                "Returns 404 if no requirement data is available for the given combination.")
            .Produces<VisaRequirementDto>(200)
            .ProducesProblem(404)
            .RequireRateLimiting("visa");
        }
    }
}
