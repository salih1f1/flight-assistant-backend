using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.ReferenceData.Entities;
using MediatR;

namespace FlightAssistant.Application.Queries.Admin.Export;

public record AirlineExportDto(
    string? Iata, string? Icao, string Name, string CountryCode, string? AllianceCode);

public record ExportAirlinesQuery : IRequest<IReadOnlyList<AirlineExportDto>>;

public sealed class ExportAirlinesQueryHandler : IRequestHandler<ExportAirlinesQuery, IReadOnlyList<AirlineExportDto>>
{
    private readonly IVerifiableRepository<Airline> _repo;
    public ExportAirlinesQueryHandler(IVerifiableRepository<Airline> repo) => _repo = repo;

    public async Task<IReadOnlyList<AirlineExportDto>> Handle(ExportAirlinesQuery request, CancellationToken ct)
    {
        var airlines = await _repo.GetAllVerifiedAsync(ct);
        return airlines.Select(a => new AirlineExportDto(
            a.Iata, a.Icao, a.Name, a.CountryCode, a.AllianceCode)).ToList();
    }
}
