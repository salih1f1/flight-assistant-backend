using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.ReferenceData.Entities;
using MediatR;

namespace FlightAssistant.Application.Commands.Admin.Import;

public record AllianceImportDto(string Code, string Name);

public record ImportAlliancesCommand(IReadOnlyList<AllianceImportDto> Items) : IRequest<ImportResult>;

public sealed class ImportAlliancesCommandHandler : IRequestHandler<ImportAlliancesCommand, ImportResult>
{
    private readonly IAllianceRepository _repo;
    public ImportAlliancesCommandHandler(IAllianceRepository repo) => _repo = repo;

    public async Task<ImportResult> Handle(ImportAlliancesCommand request, CancellationToken ct)
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
                    await _repo.AddAsync(Alliance.Create(dto.Code, dto.Name), ct);
                    inserted++;
                }
                else
                {
                    existing.Update(dto.Name);
                    updated++;
                }
            }
            catch (Exception ex)
            {
                errors.Add($"Alliance '{dto.Code}': {ex.Message}");
            }
        }

        await _repo.SaveChangesAsync(ct);
        return new ImportResult(inserted, updated, errors);
    }
}
