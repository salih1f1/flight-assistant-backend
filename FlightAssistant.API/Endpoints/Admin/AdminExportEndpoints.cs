using FlightAssistant.Application.Queries.Admin.Export;
using FlightAssistant.API.Csv;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace FlightAssistant.API.Endpoints.Admin;

public static class AdminExportEndpoints
{
    public static void MapAdminExportEndpoints(this IEndpointRouteBuilder admin)
    {
        var group = admin.MapGroup("export").WithTags("Admin - Export");

        group.MapGet("airports", async (IMediator mediator, CancellationToken ct) =>
        {
            var data = await mediator.Send(new ExportAirportsQuery(), ct);
            return Results.File(CsvExportWriter.Write<AirportExportDto, AirportExportDtoMap>(data), "text/csv", "airports-export.csv");
        }).WithName("ExportAirports").WithSummary("Export all verified airports as CSV")
            .ProducesCsvFile();

        group.MapGet("airlines", async (IMediator mediator, CancellationToken ct) =>
        {
            var data = await mediator.Send(new ExportAirlinesQuery(), ct);
            return Results.File(CsvExportWriter.Write<AirlineExportDto, AirlineExportDtoMap>(data), "text/csv", "airlines-export.csv");
        }).WithName("ExportAirlines").WithSummary("Export all verified airlines as CSV")
            .ProducesCsvFile();

        group.MapGet("alliances", async (IMediator mediator, CancellationToken ct) =>
        {
            var data = await mediator.Send(new ExportAlliancesQuery(), ct);
            return Results.File(CsvExportWriter.Write<AllianceExportDto, AllianceExportDtoMap>(data), "text/csv", "alliances-export.csv");
        }).WithName("ExportAlliances").WithSummary("Export all verified alliances as CSV")
            .ProducesCsvFile();

        group.MapGet("countries", async (IMediator mediator, CancellationToken ct) =>
        {
            var data = await mediator.Send(new ExportCountriesQuery(), ct);
            return Results.File(CsvExportWriter.Write<CountryExportDto, CountryExportDtoMap>(data), "text/csv", "countries-export.csv");
        }).WithName("ExportCountries").WithSummary("Export all verified countries as CSV")
            .ProducesCsvFile();

        group.MapGet("lounges", async (IMediator mediator, CancellationToken ct) =>
        {
            var data = await mediator.Send(new ExportLoungesQuery(), ct);
            return Results.File(CsvExportWriter.Write<LoungeExportDto, LoungeExportDtoMap>(data), "text/csv", "lounges-export.csv");
        }).WithName("ExportLounges").WithSummary("Export all verified lounges as CSV (Providers column is JSON-encoded)")
            .ProducesCsvFile();

        group.MapGet("lounge-providers", async (IMediator mediator, CancellationToken ct) =>
        {
            var data = await mediator.Send(new ExportLoungeProvidersQuery(), ct);
            return Results.File(CsvExportWriter.Write<LoungeProviderExportDto, LoungeProviderExportDtoMap>(data), "text/csv", "lounge-providers-export.csv");
        }).WithName("ExportLoungeProviders").WithSummary("Export all verified lounge providers as CSV")
            .ProducesCsvFile();

        group.MapGet("loyalty-programs", async (IMediator mediator, CancellationToken ct) =>
        {
            var data = await mediator.Send(new ExportLoyaltyProgramsQuery(), ct);
            return Results.File(CsvExportWriter.Write<LoyaltyProgramExportDto, LoyaltyProgramExportDtoMap>(data), "text/csv", "loyalty-programs-export.csv");
        }).WithName("ExportLoyaltyPrograms").WithSummary("Export all verified loyalty programs as CSV (Tiers column is JSON-encoded)")
            .ProducesCsvFile();

        group.MapGet("loyalty-program-kinds", async (IMediator mediator, CancellationToken ct) =>
        {
            var data = await mediator.Send(new ExportLoyaltyProgramKindsQuery(), ct);
            return Results.File(CsvExportWriter.Write<LoyaltyProgramKindExportDto, LoyaltyProgramKindExportDtoMap>(data), "text/csv", "loyalty-program-kinds-export.csv");
        }).WithName("ExportLoyaltyProgramKinds").WithSummary("Export all verified loyalty program kinds as CSV")
            .ProducesCsvFile();
    }

    /// <summary>
    /// The response contract every route above shares: 200 carrying a CSV file, not JSON (#414).
    /// Without it each route declared a bare <c>"200": { "description": "OK" }</c> — a generated
    /// client was told a 200 happens, but not that it brings a file, nor what to send in
    /// <c>Accept</c> or expect back in <c>Content-Type</c>.
    ///
    /// <c>FileResult</c> is what Swashbuckle recognises as a download: it renders the body as
    /// <c>type: string, format: binary</c> under <c>text/csv</c>, which is what
    /// <c>Results.File(..., "text/csv", ...)</c> actually sends. The columns are deliberately not
    /// described — they come from the <c>*ExportDto</c>/<c>*Map</c> pairs, and the import side
    /// already documents that shape as what it accepts back.
    ///
    /// Declared once rather than eight times, so the eight routes cannot drift apart.
    /// </summary>
    private static RouteHandlerBuilder ProducesCsvFile(this RouteHandlerBuilder route) =>
        route.Produces<FileResult>(StatusCodes.Status200OK, "text/csv");
}
