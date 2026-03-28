using FlightAssistant.Application.Interfaces.Repositories;
using MediatR;

namespace FlightAssistant.Application.Queries.Admin.Airports
{
    public record AirportResult(
        int Id,
        string Iata,
        string? Icao,
        string Name,
        string City,
        string CountryCode,
        double? Latitude,
        double? Longitude,
        int? ElevationFt,
        string? Timezone
    );

    public record GetAllAirportsQuery : IRequest<IReadOnlyList<AirportResult>>;

    public sealed class GetAllAirportsQueryHandler : IRequestHandler<GetAllAirportsQuery, IReadOnlyList<AirportResult>>
    {
        private readonly IAirportRepository _repo;
        public GetAllAirportsQueryHandler(IAirportRepository repo) => _repo = repo;

        public async Task<IReadOnlyList<AirportResult>> Handle(GetAllAirportsQuery request, CancellationToken ct)
        {
            var airports = await _repo.GetAllAsync(ct);
            return airports.Select(a => new AirportResult(
                a.Id, a.Iata, a.Icao, a.Name, a.City, a.CountryCode,
                a.Latitude, a.Longitude, a.ElevationFt, a.Timezone
            )).ToList();
        }
    }
}
