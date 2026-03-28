using FlightAssistant.Application.Queries.Admin.RefData;
using FlightAssistant.Contracts.Responses.Admin;
using MediatR;

namespace FlightAssistant.API.Endpoints.Admin
{
    public static class AdminRefDataEndpoints
    {
        public static void MapAdminRefDataEndpoints(this IEndpointRouteBuilder app)
        {
            var group = app.MapGroup("admin")
                .RequireAuthorization("AdminOnly")
                .WithTags("Admin - Reference Data");

            // GET /admin/countries
            group.MapGet("countries", async (IMediator mediator, CancellationToken ct) =>
            {
                var results = await mediator.Send(new GetAllCountriesQuery(), ct);
                return Results.Ok(results.Select(c => new CountryAdminDto(
                    c.Code, c.Name, c.Alpha3, c.NumericCode, c.Region, c.Subregion,
                    c.IsEU, c.IsSchengen, c.IsEEA)));
            })
            .WithName("AdminGetAllCountries")
            .WithSummary("List all countries")
            .Produces<IEnumerable<CountryAdminDto>>(200);

            // GET /admin/alliances
            group.MapGet("alliances", async (IMediator mediator, CancellationToken ct) =>
            {
                var results = await mediator.Send(new GetAllAlliancesQuery(), ct);
                return Results.Ok(results.Select(a => new AllianceAdminDto(a.Code, a.Name)));
            })
            .WithName("AdminGetAllAlliances")
            .WithSummary("List all alliances")
            .Produces<IEnumerable<AllianceAdminDto>>(200);
        }
    }
}
