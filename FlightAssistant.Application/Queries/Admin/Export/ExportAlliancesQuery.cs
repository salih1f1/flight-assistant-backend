using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.ReferenceData.Entities;
using MediatR;

namespace FlightAssistant.Application.Queries.Admin.Export;

public record AllianceExportDto(string Code, string Name);

public record ExportAlliancesQuery : IRequest<IReadOnlyList<AllianceExportDto>>;

public sealed class ExportAlliancesQueryHandler : IRequestHandler<ExportAlliancesQuery, IReadOnlyList<AllianceExportDto>>
{
    private readonly IVerifiableRepository<Alliance> _repo;
    public ExportAlliancesQueryHandler(IVerifiableRepository<Alliance> repo) => _repo = repo;

    public async Task<IReadOnlyList<AllianceExportDto>> Handle(ExportAlliancesQuery request, CancellationToken ct)
    {
        var alliances = await _repo.GetAllVerifiedAsync(ct);
        return alliances.Select(a => new AllianceExportDto(a.Code, a.Name)).ToList();
    }
}
