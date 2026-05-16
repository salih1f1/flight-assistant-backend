using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.ReferenceData.Entities;
using MediatR;

namespace FlightAssistant.Application.Commands.Admin.Import;

public record CountryImportDto(
    string Code, string Name, string? Alpha3, int? NumericCode,
    string? Region, string? Subregion, bool? IsEU, bool? IsSchengen, bool? IsEEA);

public record ImportCountriesCommand(IReadOnlyList<CountryImportDto> Items) : IRequest<ImportResult>;

public sealed class ImportCountriesCommandHandler : IRequestHandler<ImportCountriesCommand, ImportResult>
{
    private readonly ICountryRepository _repo;
    public ImportCountriesCommandHandler(ICountryRepository repo) => _repo = repo;

    public async Task<ImportResult> Handle(ImportCountriesCommand request, CancellationToken ct)
    {
        var inserted = 0;
        var updated = 0;
        var errors = new List<string>();

        foreach (var dto in request.Items)
        {
            try
            {
                var existing = await _repo.FindByCodeAsync(dto.Code, ct);
                if (existing is null)
                {
                    await _repo.AddAsync(Country.Create(dto.Code, dto.Name, dto.Alpha3, dto.NumericCode,
                        dto.Region, dto.Subregion, dto.IsEU, dto.IsSchengen, dto.IsEEA), ct);
                    inserted++;
                }
                else
                {
                    existing.Update(dto.Name, dto.Alpha3, dto.NumericCode,
                        dto.Region, dto.Subregion, dto.IsEU, dto.IsSchengen, dto.IsEEA);
                    updated++;
                }
            }
            catch (Exception ex)
            {
                errors.Add($"Country '{dto.Code}': {ex.Message}");
            }
        }

        await _repo.SaveChangesAsync(ct);
        return new ImportResult(inserted, updated, errors);
    }
}
