using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.ReferenceData.Entities;
using MediatR;

namespace FlightAssistant.Application.Queries.Admin.Export;

public record AirportExportDto(
    string Iata, string? Icao, string Name, string City, string CountryCode,
    double? Latitude, double? Longitude, int? ElevationFt, string? Timezone);

public record ExportAirportsQuery : IRequest<IReadOnlyList<AirportExportDto>>;

public sealed class ExportAirportsQueryHandler : IRequestHandler<ExportAirportsQuery, IReadOnlyList<AirportExportDto>>
{
    private readonly IVerifiableRepository<Airport> _repo;
    public ExportAirportsQueryHandler(IVerifiableRepository<Airport> repo) => _repo = repo;

    public async Task<IReadOnlyList<AirportExportDto>> Handle(ExportAirportsQuery request, CancellationToken ct)
    {
        var airports = await _repo.GetAllVerifiedAsync(ct);
        return airports.Select(a => new AirportExportDto(
            a.Iata, a.Icao, a.Name, a.City, a.CountryCode,
            a.Latitude, a.Longitude, a.ElevationFt, a.Timezone)).ToList();
    }
}
