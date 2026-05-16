using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Loyalty.Entities;
using MediatR;

namespace FlightAssistant.Application.Queries.Admin.Export;

public record LoyaltyProgramKindExportDto(string Code, string Name, string? Description);

public record ExportLoyaltyProgramKindsQuery : IRequest<IReadOnlyList<LoyaltyProgramKindExportDto>>;

public sealed class ExportLoyaltyProgramKindsQueryHandler : IRequestHandler<ExportLoyaltyProgramKindsQuery, IReadOnlyList<LoyaltyProgramKindExportDto>>
{
    private readonly IVerifiableRepository<LoyaltyProgramKind> _repo;
    public ExportLoyaltyProgramKindsQueryHandler(IVerifiableRepository<LoyaltyProgramKind> repo) => _repo = repo;

    public async Task<IReadOnlyList<LoyaltyProgramKindExportDto>> Handle(ExportLoyaltyProgramKindsQuery request, CancellationToken ct)
    {
        var kinds = await _repo.GetAllVerifiedAsync(ct);
        return kinds.Select(k => new LoyaltyProgramKindExportDto(k.Code, k.Name, k.Description)).ToList();
    }
}
