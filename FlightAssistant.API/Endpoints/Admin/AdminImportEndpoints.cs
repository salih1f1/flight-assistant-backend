using FlightAssistant.Application.Commands.Admin.Import;
using MediatR;

namespace FlightAssistant.API.Endpoints.Admin;

public static class AdminImportEndpoints
{
    public static void MapAdminImportEndpoints(this IEndpointRouteBuilder admin)
    {
        var group = admin.MapGroup("import").WithTags("Admin - Import");

        group.MapPost("alliances", async (IReadOnlyList<AllianceImportDto> body, IMediator mediator, CancellationToken ct) =>
            Results.Ok(await mediator.Send(new ImportAlliancesCommand(body), ct)))
            .WithName("ImportAlliances")
            .WithSummary("Import alliances (run before countries, airports, airlines). Upserts by Code.");

        group.MapPost("countries", async (IReadOnlyList<CountryImportDto> body, IMediator mediator, CancellationToken ct) =>
            Results.Ok(await mediator.Send(new ImportCountriesCommand(body), ct)))
            .WithName("ImportCountries")
            .WithSummary("Import countries (run after alliances). Upserts by Code.");

        group.MapPost("airports", async (IReadOnlyList<AirportImportDto> body, IMediator mediator, CancellationToken ct) =>
            Results.Ok(await mediator.Send(new ImportAirportsCommand(body), ct)))
            .WithName("ImportAirports")
            .WithSummary("Import airports (run after countries). Upserts by IATA.");

        group.MapPost("airlines", async (IReadOnlyList<AirlineImportDto> body, IMediator mediator, CancellationToken ct) =>
            Results.Ok(await mediator.Send(new ImportAirlinesCommand(body), ct)))
            .WithName("ImportAirlines")
            .WithSummary("Import airlines (run after airports). Upserts by IATA, fallback ICAO.");

        group.MapPost("lounge-providers", async (IReadOnlyList<LoungeProviderImportDto> body, IMediator mediator, CancellationToken ct) =>
            Results.Ok(await mediator.Send(new ImportLoungeProvidersCommand(body), ct)))
            .WithName("ImportLoungeProviders")
            .WithSummary("Import lounge providers (run after airlines). Upserts by Code.");

        group.MapPost("loyalty-program-kinds", async (IReadOnlyList<LoyaltyProgramKindImportDto> body, IMediator mediator, CancellationToken ct) =>
            Results.Ok(await mediator.Send(new ImportLoyaltyProgramKindsCommand(body), ct)))
            .WithName("ImportLoyaltyProgramKinds")
            .WithSummary("Import loyalty program kinds (run after airlines). Upserts by Code.");

        group.MapPost("lounges", async (IReadOnlyList<LoungeImportDto> body, IMediator mediator, CancellationToken ct) =>
            Results.Ok(await mediator.Send(new ImportLoungesCommand(body), ct)))
            .WithName("ImportLounges")
            .WithSummary("Import lounges (run after lounge-providers). Upserts by Name+AirportIata+Terminal; replaces provider links wholesale.");

        group.MapPost("loyalty-programs", async (IReadOnlyList<LoyaltyProgramImportDto> body, IMediator mediator, CancellationToken ct) =>
            Results.Ok(await mediator.Send(new ImportLoyaltyProgramsCommand(body), ct)))
            .WithName("ImportLoyaltyPrograms")
            .WithSummary("Import loyalty programs (run after loyalty-program-kinds and airlines). Upserts by Code; replaces tiers wholesale.");
    }
}
