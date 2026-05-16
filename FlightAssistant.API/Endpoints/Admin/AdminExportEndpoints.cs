using FlightAssistant.Application.Queries.Admin.Export;
using MediatR;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlightAssistant.API.Endpoints.Admin;

public static class AdminExportEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static void MapAdminExportEndpoints(this IEndpointRouteBuilder admin)
    {
        var group = admin.MapGroup("export").WithTags("Admin - Export");

        group.MapGet("airports", async (IMediator mediator, CancellationToken ct) =>
        {
            var data = await mediator.Send(new ExportAirportsQuery(), ct);
            return Results.File(JsonSerializer.SerializeToUtf8Bytes(data, JsonOptions), "application/json", "airports-export.json");
        }).WithName("ExportAirports").WithSummary("Export all verified airports as JSON").Produces<IReadOnlyList<AirportExportDto>>();

        group.MapGet("airlines", async (IMediator mediator, CancellationToken ct) =>
        {
            var data = await mediator.Send(new ExportAirlinesQuery(), ct);
            return Results.File(JsonSerializer.SerializeToUtf8Bytes(data, JsonOptions), "application/json", "airlines-export.json");
        }).WithName("ExportAirlines").WithSummary("Export all verified airlines as JSON").Produces<IReadOnlyList<AirlineExportDto>>();

        group.MapGet("alliances", async (IMediator mediator, CancellationToken ct) =>
        {
            var data = await mediator.Send(new ExportAlliancesQuery(), ct);
            return Results.File(JsonSerializer.SerializeToUtf8Bytes(data, JsonOptions), "application/json", "alliances-export.json");
        }).WithName("ExportAlliances").WithSummary("Export all verified alliances as JSON").Produces<IReadOnlyList<AllianceExportDto>>();

        group.MapGet("countries", async (IMediator mediator, CancellationToken ct) =>
        {
            var data = await mediator.Send(new ExportCountriesQuery(), ct);
            return Results.File(JsonSerializer.SerializeToUtf8Bytes(data, JsonOptions), "application/json", "countries-export.json");
        }).WithName("ExportCountries").WithSummary("Export all verified countries as JSON").Produces<IReadOnlyList<CountryExportDto>>();

        group.MapGet("lounges", async (IMediator mediator, CancellationToken ct) =>
        {
            var data = await mediator.Send(new ExportLoungesQuery(), ct);
            return Results.File(JsonSerializer.SerializeToUtf8Bytes(data, JsonOptions), "application/json", "lounges-export.json");
        }).WithName("ExportLounges").WithSummary("Export all verified lounges as JSON").Produces<IReadOnlyList<LoungeExportDto>>();

        group.MapGet("lounge-providers", async (IMediator mediator, CancellationToken ct) =>
        {
            var data = await mediator.Send(new ExportLoungeProvidersQuery(), ct);
            return Results.File(JsonSerializer.SerializeToUtf8Bytes(data, JsonOptions), "application/json", "lounge-providers-export.json");
        }).WithName("ExportLoungeProviders").WithSummary("Export all verified lounge providers as JSON").Produces<IReadOnlyList<LoungeProviderExportDto>>();

        group.MapGet("loyalty-programs", async (IMediator mediator, CancellationToken ct) =>
        {
            var data = await mediator.Send(new ExportLoyaltyProgramsQuery(), ct);
            return Results.File(JsonSerializer.SerializeToUtf8Bytes(data, JsonOptions), "application/json", "loyalty-programs-export.json");
        }).WithName("ExportLoyaltyPrograms").WithSummary("Export all verified loyalty programs as JSON").Produces<IReadOnlyList<LoyaltyProgramExportDto>>();

        group.MapGet("loyalty-program-kinds", async (IMediator mediator, CancellationToken ct) =>
        {
            var data = await mediator.Send(new ExportLoyaltyProgramKindsQuery(), ct);
            return Results.File(JsonSerializer.SerializeToUtf8Bytes(data, JsonOptions), "application/json", "loyalty-program-kinds-export.json");
        }).WithName("ExportLoyaltyProgramKinds").WithSummary("Export all verified loyalty program kinds as JSON").Produces<IReadOnlyList<LoyaltyProgramKindExportDto>>();
    }
}
