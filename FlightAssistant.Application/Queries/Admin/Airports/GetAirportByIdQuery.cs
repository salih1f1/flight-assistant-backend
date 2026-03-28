using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Application.Queries.Admin.Airports;
using MediatR;

namespace FlightAssistant.Application.Queries.Admin.Airports
{
    public record GetAirportByIdQuery(int Id) : IRequest<AirportResult?>;

    public sealed class GetAirportByIdQueryHandler : IRequestHandler<GetAirportByIdQuery, AirportResult?>
    {
        private readonly IAirportRepository _repo;
        public GetAirportByIdQueryHandler(IAirportRepository repo) => _repo = repo;

        public async Task<AirportResult?> Handle(GetAirportByIdQuery request, CancellationToken ct)
        {
            var a = await _repo.GetByIdAsync(request.Id, ct);
            if (a is null) return null;
            return new AirportResult(a.Id, a.Iata, a.Icao, a.Name, a.City, a.CountryCode,
                a.Latitude, a.Longitude, a.ElevationFt, a.Timezone);
        }
    }
}
