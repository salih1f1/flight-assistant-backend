using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Lounges.Entities;
using FlightAssistant.Domain.Lounges.Enums;
using MediatR;

namespace FlightAssistant.Application.Commands.Admin.Import;

public record LoungeProviderLinkImportDto(string ProviderCode, string Role, string? MinAllianceTierCode, string? Notes);

public record LoungeImportDto(
    string Name, string AirportIata, string? Terminal, string? LocationHint, string? Hours,
    bool IsAirside, bool IsDomesticOnly, bool IsInternationalOnly, string? Amenities,
    IReadOnlyList<LoungeProviderLinkImportDto> Providers);

public record ImportLoungesCommand(IReadOnlyList<LoungeImportDto> Items) : IRequest<ImportResult>;

public sealed class ImportLoungesCommandHandler : IRequestHandler<ImportLoungesCommand, ImportResult>
{
    private readonly ILoungeRepository _loungeRepo;
    private readonly IRefDataRepository _refDataRepo;

    public ImportLoungesCommandHandler(ILoungeRepository loungeRepo, IRefDataRepository refDataRepo)
    {
        _loungeRepo = loungeRepo;
        _refDataRepo = refDataRepo;
    }

    public async Task<ImportResult> Handle(ImportLoungesCommand request, CancellationToken ct)
    {
        var inserted = 0;
        var updated = 0;
        var errors = new List<string>();

        foreach (var dto in request.Items)
        {
            try
            {
                var airportId = await _refDataRepo.GetAirportIdByIataAsync(dto.AirportIata, ct);
                if (airportId is null)
                {
                    errors.Add($"Lounge '{dto.Name}': airport '{dto.AirportIata}' not found — skipped");
                    continue;
                }

                var providerLinks = await ResolveProviderLinksAsync(dto, errors, ct);

                var existing = await _loungeRepo.FindByNaturalKeyWithProvidersAsync(airportId.Value, dto.Name, dto.Terminal, ct);
                if (existing is null)
                {
                    var lounge = Lounge.Create(airportId.Value, dto.Name, dto.Terminal, dto.LocationHint,
                        dto.Hours, dto.IsAirside, dto.IsDomesticOnly, dto.IsInternationalOnly, dto.Amenities);
                    foreach (var link in providerLinks)
                        lounge.Providers.Add(link);
                    await _loungeRepo.AddAsync(lounge, ct);
                    inserted++;
                }
                else
                {
                    existing.Update(dto.Name, dto.Terminal, dto.LocationHint, dto.Hours,
                        dto.IsAirside, dto.IsDomesticOnly, dto.IsInternationalOnly, dto.Amenities);
                    existing.Providers.Clear();
                    foreach (var link in providerLinks)
                        existing.Providers.Add(link);
                    updated++;
                }
            }
            catch (Exception ex)
            {
                errors.Add($"Lounge '{dto.Name}': {ex.Message}");
            }
        }

        await _loungeRepo.SaveChangesAsync(ct);
        return new ImportResult(inserted, updated, errors);
    }

    private async Task<List<LoungeProviderLink>> ResolveProviderLinksAsync(
        LoungeImportDto dto, List<string> errors, CancellationToken ct)
    {
        var links = new List<LoungeProviderLink>();
        foreach (var p in dto.Providers)
        {
            if (!Enum.TryParse<LoungeProviderRole>(p.Role, ignoreCase: true, out var role))
            {
                errors.Add($"Lounge '{dto.Name}': unknown provider role '{p.Role}' for '{p.ProviderCode}' — skipped");
                continue;
            }

            var provider = await _loungeRepo.FindProviderByCodeAsync(p.ProviderCode, ct);
            if (provider is null)
            {
                errors.Add($"Lounge '{dto.Name}': provider '{p.ProviderCode}' not found — skipped");
                continue;
            }

            links.Add(LoungeProviderLink.Create(0, provider.Id, role, p.Notes, p.MinAllianceTierCode));
        }
        return links;
    }
}
