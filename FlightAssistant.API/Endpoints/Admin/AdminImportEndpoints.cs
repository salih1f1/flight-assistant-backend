using FlightAssistant.Application.Administration;
using FlightAssistant.Application.Commands.Admin.Import;
using FlightAssistant.API.Csv;
using FlightAssistant.Domain.Administration.Enums;
using MediatR;

namespace FlightAssistant.API.Endpoints.Admin;

public static class AdminImportEndpoints
{
    /// <summary>
    /// The verification behaviour (#362) shared by all 8 import endpoints — kept out of each
    /// route's <c>.WithSummary(...)</c> so the summary stays a one-line description, and out of
    /// each's own duplicated string so this can't drift between endpoints.
    /// </summary>
    private const string VerificationDescription =
        "Every row ends verified: kept as-is when the CSV's VerifiedAt/VerifiedBy/VerificationSource " +
        "columns are all present, stamped `import` when none are present, or rejected into Errors " +
        "(207, the rest of the file still imports) when only some are present.";

    public static void MapAdminImportEndpoints(this IEndpointRouteBuilder admin)
    {
        var group = admin.MapGroup("import").WithTags("Admin - Import")
            .DisableAntiforgery()
            .AddEndpointFilter<CsvFileUploadValidationFilter>()
            // CSV import bulk-upserts rows, which are otherwise indistinguishable from an ordinary
            // create/update once they hit the AdminAuditInterceptor — this is the one caller that
            // overrides the derived action so every row from an import is recorded as Import.
            .AddEndpointFilter(async (efiContext, next) =>
            {
                var adminContext = efiContext.HttpContext.RequestServices.GetRequiredService<AdminContext>();
                adminContext.ActionOverride = AdminAuditAction.Import;
                return await next(efiContext);
            });

        group.MapPost("alliances", async Task<IResult> (IFormFile file, IMediator mediator, CancellationToken ct) =>
        {
            await using var stream = file.OpenReadStream();
            var items = await CsvImportParser.ParseAsync<AllianceImportDto, AllianceImportDtoMap>(stream);
            var result = await mediator.Send(new ImportAlliancesCommand(items), ct);
            return result.ToImportResponse();
        }).WithName("ImportAlliances")
          .WithSummary("Import alliances from CSV (run before countries, airports, airlines). Upserts by Code.")
          .WithDescription(VerificationDescription)
          .Produces<ImportResult>(200)
          .Produces<ImportResult>(207)
          .ProducesValidationProblem();

        group.MapPost("countries", async Task<IResult> (IFormFile file, IMediator mediator, CancellationToken ct) =>
        {
            await using var stream = file.OpenReadStream();
            var items = await CsvImportParser.ParseAsync<CountryImportDto, CountryImportDtoMap>(stream);
            var result = await mediator.Send(new ImportCountriesCommand(items), ct);
            return result.ToImportResponse();
        }).WithName("ImportCountries")
          .WithSummary("Import countries from CSV (run after alliances). Upserts by Code.")
          .WithDescription(VerificationDescription)
          .Produces<ImportResult>(200)
          .Produces<ImportResult>(207)
          .ProducesValidationProblem();

        group.MapPost("airports", async Task<IResult> (IFormFile file, IMediator mediator, CancellationToken ct) =>
        {
            await using var stream = file.OpenReadStream();
            var items = await CsvImportParser.ParseAsync<AirportImportDto, AirportImportDtoMap>(stream, rejectHeaders: ["Timezone"]);
            var result = await mediator.Send(new ImportAirportsCommand(items), ct);
            return result.ToImportResponse();
        }).WithName("ImportAirports")
          .WithSummary("Import airports from CSV (run after countries). Upserts by IATA.")
          .WithDescription(VerificationDescription)
          .Produces<ImportResult>(200)
          .Produces<ImportResult>(207)
          .ProducesValidationProblem();

        group.MapPost("airlines", async Task<IResult> (IFormFile file, IMediator mediator, CancellationToken ct) =>
        {
            await using var stream = file.OpenReadStream();
            var items = await CsvImportParser.ParseAsync<AirlineImportDto, AirlineImportDtoMap>(stream);
            var result = await mediator.Send(new ImportAirlinesCommand(items), ct);
            return result.ToImportResponse();
        }).WithName("ImportAirlines")
          .WithSummary("Import airlines from CSV (run after airports). Upserts by IATA, fallback ICAO.")
          .WithDescription(VerificationDescription)
          .Produces<ImportResult>(200)
          .Produces<ImportResult>(207)
          .ProducesValidationProblem();

        group.MapPost("lounge-providers", async Task<IResult> (IFormFile file, IMediator mediator, CancellationToken ct) =>
        {
            await using var stream = file.OpenReadStream();
            var items = await CsvImportParser.ParseAsync<LoungeProviderImportDto, LoungeProviderImportDtoMap>(stream);
            var result = await mediator.Send(new ImportLoungeProvidersCommand(items), ct);
            return result.ToImportResponse();
        }).WithName("ImportLoungeProviders")
          .WithSummary("Import lounge providers from CSV (run after airlines). Upserts by Code.")
          .WithDescription(VerificationDescription)
          .Produces<ImportResult>(200)
          .Produces<ImportResult>(207)
          .ProducesValidationProblem();

        group.MapPost("loyalty-program-kinds", async Task<IResult> (IFormFile file, IMediator mediator, CancellationToken ct) =>
        {
            await using var stream = file.OpenReadStream();
            var items = await CsvImportParser.ParseAsync<LoyaltyProgramKindImportDto, LoyaltyProgramKindImportDtoMap>(stream);
            var result = await mediator.Send(new ImportLoyaltyProgramKindsCommand(items), ct);
            return result.ToImportResponse();
        }).WithName("ImportLoyaltyProgramKinds")
          .WithSummary("Import loyalty program kinds from CSV (run after airlines). Upserts by Code.")
          .WithDescription(VerificationDescription)
          .Produces<ImportResult>(200)
          .Produces<ImportResult>(207)
          .ProducesValidationProblem();

        group.MapPost("lounges", async Task<IResult> (IFormFile file, IMediator mediator, CancellationToken ct) =>
        {
            await using var stream = file.OpenReadStream();
            var items = await CsvImportParser.ParseAsync<LoungeImportDto, LoungeImportDtoMap>(stream);
            var result = await mediator.Send(new ImportLoungesCommand(items), ct);
            return result.ToImportResponse();
        }).WithName("ImportLounges")
          .WithSummary("Import lounges from CSV (run after lounge-providers). Providers column is JSON-encoded. Upserts by Name+AirportIata+Terminal.")
          .WithDescription(VerificationDescription)
          .Produces<ImportResult>(200)
          .Produces<ImportResult>(207)
          .ProducesValidationProblem();

        group.MapPost("loyalty-programs", async Task<IResult> (IFormFile file, IMediator mediator, CancellationToken ct) =>
        {
            await using var stream = file.OpenReadStream();
            var items = await CsvImportParser.ParseAsync<LoyaltyProgramImportDto, LoyaltyProgramImportDtoMap>(stream);
            var result = await mediator.Send(new ImportLoyaltyProgramsCommand(items), ct);
            return result.ToImportResponse();
        }).WithName("ImportLoyaltyPrograms")
          .WithSummary("Import loyalty programs from CSV (run after loyalty-program-kinds and airlines). Tiers column is JSON-encoded. Upserts by Code.")
          .WithDescription(VerificationDescription)
          .Produces<ImportResult>(200)
          .Produces<ImportResult>(207)
          .ProducesValidationProblem();
    }
}
