using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Lounges.Entities;
using MediatR;

namespace FlightAssistant.Application.Queries.Admin.Export;

public record LoungeProviderExportDto(
    string Code, string Name, string Kind, string? Description,
    DateTime? VerifiedAt, string? VerifiedBy, string? VerificationSource);

public record ExportLoungeProvidersQuery : IRequest<IReadOnlyList<LoungeProviderExportDto>>;

public sealed class ExportLoungeProvidersQueryHandler : IRequestHandler<ExportLoungeProvidersQuery, IReadOnlyList<LoungeProviderExportDto>>
{
    private readonly IVerifiableRepository<LoungeProvider> _repo;
    public ExportLoungeProvidersQueryHandler(IVerifiableRepository<LoungeProvider> repo) => _repo = repo;

    public async Task<IReadOnlyList<LoungeProviderExportDto>> Handle(ExportLoungeProvidersQuery request, CancellationToken ct)
    {
        var providers = await _repo.GetAllVerifiedAsync(ct);
        return providers.Select(p => new LoungeProviderExportDto(
            p.Code, p.Name, p.Kind.ToString(), p.Description,
            p.VerifiedAt, p.VerifiedBy, p.VerificationSource)).ToList();
    }
}
