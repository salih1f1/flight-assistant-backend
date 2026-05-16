using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.ReferenceData.Entities;
using MediatR;

namespace FlightAssistant.Application.Commands.Admin.Import;

public record AirlineImportDto(string? Iata, string? Icao, string Name, string CountryCode, string? AllianceCode);

public record ImportAirlinesCommand(IReadOnlyList<AirlineImportDto> Items) : IRequest<ImportResult>;

public sealed class ImportAirlinesCommandHandler : IRequestHandler<ImportAirlinesCommand, ImportResult>
{
    private readonly IAirlineRepository _repo;
    public ImportAirlinesCommandHandler(IAirlineRepository repo) => _repo = repo;

    public async Task<ImportResult> Handle(ImportAirlinesCommand request, CancellationToken ct)
    {
        var inserted = 0;
        var updated = 0;
        var errors = new List<string>();

        foreach (var dto in request.Items)
        {
            try
            {
                Airline? existing = null;
                if (!string.IsNullOrWhiteSpace(dto.Iata))
                    existing = await _repo.FindByIataAsync(dto.Iata, ct);
                if (existing is null && !string.IsNullOrWhiteSpace(dto.Icao))
                    existing = await _repo.FindByIcaoAsync(dto.Icao, ct);

                if (existing is null)
                {
                    await _repo.AddAsync(Airline.Create(dto.Iata, dto.Icao, dto.Name, dto.CountryCode, dto.AllianceCode), ct);
                    inserted++;
                }
                else
                {
                    existing.Update(dto.Iata, dto.Icao, dto.Name, dto.CountryCode, dto.AllianceCode);
                    updated++;
                }
            }
            catch (Exception ex)
            {
                errors.Add($"Airline '{dto.Iata ?? dto.Icao ?? dto.Name}': {ex.Message}");
            }
        }

        await _repo.SaveChangesAsync(ct);
        return new ImportResult(inserted, updated, errors);
    }
}
