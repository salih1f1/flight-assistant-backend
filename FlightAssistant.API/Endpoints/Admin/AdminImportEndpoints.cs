using FlightAssistant.Application.Commands.Admin.Import;
using FlightAssistant.API.Csv;
using MediatR;

namespace FlightAssistant.API.Endpoints.Admin;

public static class AdminImportEndpoints
{
    private const long MaxFileSizeBytes = 10 * 1024 * 1024; // 10 MB

    public static void MapAdminImportEndpoints(this IEndpointRouteBuilder admin)
    {
        var group = admin.MapGroup("import").WithTags("Admin - Import")
            .DisableAntiforgery();

        group.MapPost("alliances", async Task<IResult> (IFormFile file, IMediator mediator, CancellationToken ct) =>
        {
            if (!file.ContentType.Equals("text/csv", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest("Only text/csv files are accepted.");
            if (file.Length > MaxFileSizeBytes)
                return Results.BadRequest("File size exceeds the 10 MB limit.");

            await using var stream = file.OpenReadStream();
            var items = await CsvImportParser.ParseAsync<AllianceImportDto, AllianceImportDtoMap>(stream);
            return Results.Ok(await mediator.Send(new ImportAlliancesCommand(items), ct));
        }).WithName("ImportAlliances")
          .WithSummary("Import alliances from CSV (run before countries, airports, airlines). Upserts by Code.");

        group.MapPost("countries", async Task<IResult> (IFormFile file, IMediator mediator, CancellationToken ct) =>
        {
            if (!file.ContentType.Equals("text/csv", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest("Only text/csv files are accepted.");
            if (file.Length > MaxFileSizeBytes)
                return Results.BadRequest("File size exceeds the 10 MB limit.");

            await using var stream = file.OpenReadStream();
            var items = await CsvImportParser.ParseAsync<CountryImportDto, CountryImportDtoMap>(stream);
            return Results.Ok(await mediator.Send(new ImportCountriesCommand(items), ct));
        }).WithName("ImportCountries")
          .WithSummary("Import countries from CSV (run after alliances). Upserts by Code.");

        group.MapPost("airports", async Task<IResult> (IFormFile file, IMediator mediator, CancellationToken ct) =>
        {
            if (!file.ContentType.Equals("text/csv", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest("Only text/csv files are accepted.");
            if (file.Length > MaxFileSizeBytes)
                return Results.BadRequest("File size exceeds the 10 MB limit.");

            await using var stream = file.OpenReadStream();
            var items = await CsvImportParser.ParseAsync<AirportImportDto, AirportImportDtoMap>(stream);
            return Results.Ok(await mediator.Send(new ImportAirportsCommand(items), ct));
        }).WithName("ImportAirports")
          .WithSummary("Import airports from CSV (run after countries). Upserts by IATA.");

        group.MapPost("airlines", async Task<IResult> (IFormFile file, IMediator mediator, CancellationToken ct) =>
        {
            if (!file.ContentType.Equals("text/csv", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest("Only text/csv files are accepted.");
            if (file.Length > MaxFileSizeBytes)
                return Results.BadRequest("File size exceeds the 10 MB limit.");

            await using var stream = file.OpenReadStream();
            var items = await CsvImportParser.ParseAsync<AirlineImportDto, AirlineImportDtoMap>(stream);
            return Results.Ok(await mediator.Send(new ImportAirlinesCommand(items), ct));
        }).WithName("ImportAirlines")
          .WithSummary("Import airlines from CSV (run after airports). Upserts by IATA, fallback ICAO.");

        group.MapPost("lounge-providers", async Task<IResult> (IFormFile file, IMediator mediator, CancellationToken ct) =>
        {
            if (!file.ContentType.Equals("text/csv", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest("Only text/csv files are accepted.");
            if (file.Length > MaxFileSizeBytes)
                return Results.BadRequest("File size exceeds the 10 MB limit.");

            await using var stream = file.OpenReadStream();
            var items = await CsvImportParser.ParseAsync<LoungeProviderImportDto, LoungeProviderImportDtoMap>(stream);
            return Results.Ok(await mediator.Send(new ImportLoungeProvidersCommand(items), ct));
        }).WithName("ImportLoungeProviders")
          .WithSummary("Import lounge providers from CSV (run after airlines). Upserts by Code.");

        group.MapPost("loyalty-program-kinds", async Task<IResult> (IFormFile file, IMediator mediator, CancellationToken ct) =>
        {
            if (!file.ContentType.Equals("text/csv", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest("Only text/csv files are accepted.");
            if (file.Length > MaxFileSizeBytes)
                return Results.BadRequest("File size exceeds the 10 MB limit.");

            await using var stream = file.OpenReadStream();
            var items = await CsvImportParser.ParseAsync<LoyaltyProgramKindImportDto, LoyaltyProgramKindImportDtoMap>(stream);
            return Results.Ok(await mediator.Send(new ImportLoyaltyProgramKindsCommand(items), ct));
        }).WithName("ImportLoyaltyProgramKinds")
          .WithSummary("Import loyalty program kinds from CSV (run after airlines). Upserts by Code.");

        group.MapPost("lounges", async Task<IResult> (IFormFile file, IMediator mediator, CancellationToken ct) =>
        {
            if (!file.ContentType.Equals("text/csv", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest("Only text/csv files are accepted.");
            if (file.Length > MaxFileSizeBytes)
                return Results.BadRequest("File size exceeds the 10 MB limit.");

            await using var stream = file.OpenReadStream();
            var items = await CsvImportParser.ParseAsync<LoungeImportDto, LoungeImportDtoMap>(stream);
            return Results.Ok(await mediator.Send(new ImportLoungesCommand(items), ct));
        }).WithName("ImportLounges")
          .WithSummary("Import lounges from CSV (run after lounge-providers). Providers column is JSON-encoded. Upserts by Name+AirportIata+Terminal.");

        group.MapPost("loyalty-programs", async Task<IResult> (IFormFile file, IMediator mediator, CancellationToken ct) =>
        {
            if (!file.ContentType.Equals("text/csv", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest("Only text/csv files are accepted.");
            if (file.Length > MaxFileSizeBytes)
                return Results.BadRequest("File size exceeds the 10 MB limit.");

            await using var stream = file.OpenReadStream();
            var items = await CsvImportParser.ParseAsync<LoyaltyProgramImportDto, LoyaltyProgramImportDtoMap>(stream);
            return Results.Ok(await mediator.Send(new ImportLoyaltyProgramsCommand(items), ct));
        }).WithName("ImportLoyaltyPrograms")
          .WithSummary("Import loyalty programs from CSV (run after loyalty-program-kinds and airlines). Tiers column is JSON-encoded. Upserts by Code.");
    }
}
