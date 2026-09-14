using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.ReferenceData.Entities;
using MediatR;

namespace FlightAssistant.Application.Queries.Admin.Export;

public record CountryExportDto(
    string Code, string Name, string? Alpha3, int? NumericCode,
    string? Region, string? Subregion, bool? IsEU, bool? IsSchengen, bool? IsEEA,
    DateTime? VerifiedAt, string? VerifiedBy, string? VerificationSource);

public record ExportCountriesQuery : IRequest<IReadOnlyList<CountryExportDto>>;

public sealed class ExportCountriesQueryHandler : IRequestHandler<ExportCountriesQuery, IReadOnlyList<CountryExportDto>>
{
    private readonly IVerifiableRepository<Country> _repo;
    public ExportCountriesQueryHandler(IVerifiableRepository<Country> repo) => _repo = repo;

    public async Task<IReadOnlyList<CountryExportDto>> Handle(ExportCountriesQuery request, CancellationToken ct)
    {
        var countries = await _repo.GetAllVerifiedAsync(ct);
        return countries.Select(c => new CountryExportDto(
            c.Code, c.Name, c.Alpha3, c.NumericCode,
            c.Region, c.Subregion, c.IsEU, c.IsSchengen, c.IsEEA,
            c.VerifiedAt, c.VerifiedBy, c.VerificationSource)).ToList();
    }
}
