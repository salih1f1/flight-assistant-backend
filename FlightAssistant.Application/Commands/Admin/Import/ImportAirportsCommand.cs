using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.ReferenceData.Entities;
using MediatR;

namespace FlightAssistant.Application.Commands.Admin.Import;

public record AirportImportDto(
    string Iata, string? Icao, string Name, string City, string CountryCode,
    double? Latitude, double? Longitude, int? ElevationFt, string? Timezone);

public record ImportAirportsCommand(IReadOnlyList<AirportImportDto> Items) : IRequest<ImportResult>;

public sealed class ImportAirportsCommandHandler : IRequestHandler<ImportAirportsCommand, ImportResult>
{
    private readonly IAirportRepository _repo;
    public ImportAirportsCommandHandler(IAirportRepository repo) => _repo = repo;

    public async Task<ImportResult> Handle(ImportAirportsCommand request, CancellationToken ct)
    {
        var inserted = 0;
        var updated = 0;
        var errors = new List<string>();

        foreach (var dto in request.Items)
        {
            try
            {
                var existing = await _repo.FindByIataAsync(dto.Iata, ct);
                if (existing is null)
                {
                    await _repo.AddAsync(Airport.Create(dto.Iata, dto.Icao, dto.Name, dto.City, dto.CountryCode,
                        dto.Latitude, dto.Longitude, dto.ElevationFt, dto.Timezone), ct);
                    inserted++;
                }
                else
                {
                    existing.Update(dto.Iata, dto.Icao, dto.Name, dto.City, dto.CountryCode,
                        dto.Latitude, dto.Longitude, dto.ElevationFt, dto.Timezone);
                    updated++;
                }
            }
            catch (Exception ex)
            {
                errors.Add($"Airport '{dto.Iata}': {ex.Message}");
            }
        }

        await _repo.SaveChangesAsync(ct);
        return new ImportResult(inserted, updated, errors);
    }
}
