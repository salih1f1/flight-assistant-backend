using FlightAssistant.Application.Interfaces.Repositories;
using MediatR;

namespace FlightAssistant.Application.Queries.Admin.Export;

public record LoungeProviderLinkExportDto(string ProviderCode, string Role, string? MinAllianceTierCode, string? Notes);

public record LoungeExportDto(
    string Name, string AirportIata, string? Terminal, string? LocationHint, string? Hours,
    bool IsAirside, bool IsDomesticOnly, bool IsInternationalOnly, string? Amenities,
    IReadOnlyList<LoungeProviderLinkExportDto> Providers);

public record ExportLoungesQuery : IRequest<IReadOnlyList<LoungeExportDto>>;

public sealed class ExportLoungesQueryHandler : IRequestHandler<ExportLoungesQuery, IReadOnlyList<LoungeExportDto>>
{
    private readonly ILoungeRepository _loungeRepo;
    private readonly IRefDataRepository _refDataRepo;

    public ExportLoungesQueryHandler(ILoungeRepository loungeRepo, IRefDataRepository refDataRepo)
    {
        _loungeRepo = loungeRepo;
        _refDataRepo = refDataRepo;
    }

    public async Task<IReadOnlyList<LoungeExportDto>> Handle(ExportLoungesQuery request, CancellationToken ct)
    {
        var lounges = await _loungeRepo.GetAllVerifiedWithLinksAsync(ct);
        if (lounges.Count == 0) return [];

        var airportIds = lounges.Select(l => l.AirportId).Distinct().ToList();
        var airportMap = await _refDataRepo.GetAirportIataMapAsync(airportIds, ct);

        var providerIds = lounges.SelectMany(l => l.Providers).Select(p => p.ProviderId).Distinct().ToList();
        var providerCodeMap = providerIds.Count > 0
            ? await _refDataRepo.GetLoungeProviderCodeMapAsync(providerIds, ct)
            : new Dictionary<int, string>();

        return lounges.Select(l => new LoungeExportDto(
            l.Name,
            airportMap.GetValueOrDefault(l.AirportId, l.AirportId.ToString()),
            l.Terminal, l.LocationHint, l.Hours,
            l.IsAirside, l.IsDomesticOnly, l.IsInternationalOnly, l.Amenities,
            l.Providers.Select(p => new LoungeProviderLinkExportDto(
                providerCodeMap.GetValueOrDefault(p.ProviderId, p.ProviderId.ToString()),
                p.Role.ToString(),
                p.MinAllianceTierCode,
                p.Notes)).ToList()
        )).ToList();
    }
}
