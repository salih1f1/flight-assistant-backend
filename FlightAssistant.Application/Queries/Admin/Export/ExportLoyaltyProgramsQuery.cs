using FlightAssistant.Application.Interfaces.Repositories;
using MediatR;

namespace FlightAssistant.Application.Queries.Admin.Export;

public record LoyaltyTierExportDto(string Code, string Name, int Rank, string? AllianceCode, string? AllianceTierCode);

public record LoyaltyProgramExportDto(
    string Code, string Name, string KindCode,
    string? AirlineIata, string? AllianceCode,
    IReadOnlyList<LoyaltyTierExportDto> Tiers);

public record ExportLoyaltyProgramsQuery : IRequest<IReadOnlyList<LoyaltyProgramExportDto>>;

public sealed class ExportLoyaltyProgramsQueryHandler : IRequestHandler<ExportLoyaltyProgramsQuery, IReadOnlyList<LoyaltyProgramExportDto>>
{
    private readonly ILoyaltyRepository _loyaltyRepo;
    private readonly IRefDataRepository _refDataRepo;

    public ExportLoyaltyProgramsQueryHandler(ILoyaltyRepository loyaltyRepo, IRefDataRepository refDataRepo)
    {
        _loyaltyRepo = loyaltyRepo;
        _refDataRepo = refDataRepo;
    }

    public async Task<IReadOnlyList<LoyaltyProgramExportDto>> Handle(ExportLoyaltyProgramsQuery request, CancellationToken ct)
    {
        var programs = await _loyaltyRepo.GetAllVerifiedWithTiersAsync(ct);
        var kindMap = await _loyaltyRepo.GetKindCodeMapAsync(ct);

        var airlineIds = programs
            .Where(p => p.AirlineId.HasValue)
            .Select(p => p.AirlineId!.Value)
            .Distinct().ToList();
        var airlineMap = airlineIds.Count > 0
            ? await _refDataRepo.GetAirlineIataMapAsync(airlineIds, ct)
            : new Dictionary<int, string>();

        return programs.Select(p => new LoyaltyProgramExportDto(
            p.Code, p.Name,
            kindMap.GetValueOrDefault(p.KindId, p.KindId.ToString()),
            p.AirlineId.HasValue ? airlineMap.GetValueOrDefault(p.AirlineId.Value) : null,
            p.AllianceCode,
            p.Tiers.Select(t => new LoyaltyTierExportDto(
                t.Code, t.Name, t.Rank, t.AllianceCode, t.AllianceTierCode)).ToList()
        )).ToList();
    }
}
