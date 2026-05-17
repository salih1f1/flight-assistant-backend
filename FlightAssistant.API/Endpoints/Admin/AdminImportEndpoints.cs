using FlightAssistant.Application.Commands.Admin.Import;
using FlightAssistant.API.Csv;
using MediatR;

namespace FlightAssistant.API.Endpoints.Admin;

public static class AdminImportEndpoints
{
    public static void MapAdminImportEndpoints(this IEndpointRouteBuilder admin)
    {
        var group = admin.MapGroup("import").WithTags("Admin - Import")
            .DisableAntiforgery();

        group.MapPost("alliances", async (IFormFile file, IMediator mediator, CancellationToken ct) =>
        {
            var items = await CsvImportParser.ParseAsync<AllianceImportDto, AllianceImportDtoMap>(file.OpenReadStream());
            return Results.Ok(await mediator.Send(new ImportAlliancesCommand(items), ct));
        }).WithName("ImportAlliances")
          .WithSummary("Import alliances from CSV (run before countries, airports, airlines). Upserts by Code.")
          .Accepts<IFormFile>("text/csv");

        group.MapPost("countries", async (IFormFile file, IMediator mediator, CancellationToken ct) =>
        {
            var items = await CsvImportParser.ParseAsync<CountryImportDto, CountryImportDtoMap>(file.OpenReadStream());
            return Results.Ok(await mediator.Send(new ImportCountriesCommand(items), ct));
        }).WithName("ImportCountries")
          .WithSummary("Import countries from CSV (run after alliances). Upserts by Code.")
          .Accepts<IFormFile>("text/csv");

        group.MapPost("airports", async (IFormFile file, IMediator mediator, CancellationToken ct) =>
        {
            var items = await CsvImportParser.ParseAsync<AirportImportDto, AirportImportDtoMap>(file.OpenReadStream());
            return Results.Ok(await mediator.Send(new ImportAirportsCommand(items), ct));
        }).WithName("ImportAirports")
          .WithSummary("Import airports from CSV (run after countries). Upserts by IATA.")
          .Accepts<IFormFile>("text/csv");

        group.MapPost("airlines", async (IFormFile file, IMediator mediator, CancellationToken ct) =>
        {
            var items = await CsvImportParser.ParseAsync<AirlineImportDto, AirlineImportDtoMap>(file.OpenReadStream());
            return Results.Ok(await mediator.Send(new ImportAirlinesCommand(items), ct));
        }).WithName("ImportAirlines")
          .WithSummary("Import airlines from CSV (run after airports). Upserts by IATA, fallback ICAO.")
          .Accepts<IFormFile>("text/csv");

        group.MapPost("lounge-providers", async (IFormFile file, IMediator mediator, CancellationToken ct) =>
        {
            var items = await CsvImportParser.ParseAsync<LoungeProviderImportDto, LoungeProviderImportDtoMap>(file.OpenReadStream());
            return Results.Ok(await mediator.Send(new ImportLoungeProvidersCommand(items), ct));
        }).WithName("ImportLoungeProviders")
          .WithSummary("Import lounge providers from CSV (run after airlines). Upserts by Code.")
          .Accepts<IFormFile>("text/csv");

        group.MapPost("loyalty-program-kinds", async (IFormFile file, IMediator mediator, CancellationToken ct) =>
        {
            var items = await CsvImportParser.ParseAsync<LoyaltyProgramKindImportDto, LoyaltyProgramKindImportDtoMap>(file.OpenReadStream());
            return Results.Ok(await mediator.Send(new ImportLoyaltyProgramKindsCommand(items), ct));
        }).WithName("ImportLoyaltyProgramKinds")
          .WithSummary("Import loyalty program kinds from CSV (run after airlines). Upserts by Code.")
          .Accepts<IFormFile>("text/csv");

        group.MapPost("lounges", async (IFormFile file, IMediator mediator, CancellationToken ct) =>
        {
            var items = await CsvImportParser.ParseAsync<LoungeImportDto, LoungeImportDtoMap>(file.OpenReadStream());
            return Results.Ok(await mediator.Send(new ImportLoungesCommand(items), ct));
        }).WithName("ImportLounges")
          .WithSummary("Import lounges from CSV (run after lounge-providers). Providers column is JSON-encoded. Upserts by Name+AirportIata+Terminal.")
          .Accepts<IFormFile>("text/csv");

        group.MapPost("loyalty-programs", async (IFormFile file, IMediator mediator, CancellationToken ct) =>
        {
            var items = await CsvImportParser.ParseAsync<LoyaltyProgramImportDto, LoyaltyProgramImportDtoMap>(file.OpenReadStream());
            return Results.Ok(await mediator.Send(new ImportLoyaltyProgramsCommand(items), ct));
        }).WithName("ImportLoyaltyPrograms")
          .WithSummary("Import loyalty programs from CSV (run after loyalty-program-kinds and airlines). Tiers column is JSON-encoded. Upserts by Code.")
          .Accepts<IFormFile>("text/csv");
    }
}
