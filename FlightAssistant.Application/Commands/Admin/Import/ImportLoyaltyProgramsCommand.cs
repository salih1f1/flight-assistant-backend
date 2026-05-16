using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Loyalty.Entities;
using MediatR;

namespace FlightAssistant.Application.Commands.Admin.Import;

public record LoyaltyTierImportDto(string Code, string Name, int Rank, string? AllianceCode, string? AllianceTierCode);

public record LoyaltyProgramImportDto(
    string Code, string Name, string KindCode,
    string? AirlineIata, string? AllianceCode,
    IReadOnlyList<LoyaltyTierImportDto> Tiers);

public record ImportLoyaltyProgramsCommand(IReadOnlyList<LoyaltyProgramImportDto> Items) : IRequest<ImportResult>;

public sealed class ImportLoyaltyProgramsCommandHandler : IRequestHandler<ImportLoyaltyProgramsCommand, ImportResult>
{
    private readonly ILoyaltyRepository _loyaltyRepo;
    private readonly IRefDataRepository _refDataRepo;

    public ImportLoyaltyProgramsCommandHandler(ILoyaltyRepository loyaltyRepo, IRefDataRepository refDataRepo)
    {
        _loyaltyRepo = loyaltyRepo;
        _refDataRepo = refDataRepo;
    }

    public async Task<ImportResult> Handle(ImportLoyaltyProgramsCommand request, CancellationToken ct)
    {
        var inserted = 0;
        var updated = 0;
        var errors = new List<string>();

        foreach (var dto in request.Items)
        {
            try
            {
                var kind = await _loyaltyRepo.FindKindByCodeAsync(dto.KindCode, ct);
                if (kind is null)
                {
                    errors.Add($"LoyaltyProgram '{dto.Code}': kind '{dto.KindCode}' not found — skipped");
                    continue;
                }

                int? airlineId = null;
                if (!string.IsNullOrWhiteSpace(dto.AirlineIata))
                {
                    airlineId = await _refDataRepo.GetAirlineIdByIataAsync(dto.AirlineIata, ct);
                    if (airlineId is null)
                    {
                        errors.Add($"LoyaltyProgram '{dto.Code}': airline '{dto.AirlineIata}' not found — skipped");
                        continue;
                    }
                }

                var existing = await _loyaltyRepo.FindProgramByCodeWithTiersAsync(dto.Code, ct);
                if (existing is null)
                {
                    var program = LoyaltyProgram.Create(dto.Code, dto.Name, kind.Id, airlineId, dto.AllianceCode);
                    foreach (var t in dto.Tiers)
                        program.AddTier(LoyaltyTier.Create(0, t.Code, t.Name, t.Rank, t.AllianceCode, t.AllianceTierCode));
                    await _loyaltyRepo.AddProgramAsync(program, ct);
                    inserted++;
                }
                else
                {
                    existing.Update(dto.Code, dto.Name, kind.Id, airlineId, dto.AllianceCode);
                    existing.Tiers.Clear();
                    foreach (var t in dto.Tiers)
                        existing.Tiers.Add(LoyaltyTier.Create(existing.Id, t.Code, t.Name, t.Rank, t.AllianceCode, t.AllianceTierCode));
                    updated++;
                }
            }
            catch (Exception ex)
            {
                errors.Add($"LoyaltyProgram '{dto.Code}': {ex.Message}");
            }
        }

        await _loyaltyRepo.SaveChangesAsync(ct);
        return new ImportResult(inserted, updated, errors);
    }
}
