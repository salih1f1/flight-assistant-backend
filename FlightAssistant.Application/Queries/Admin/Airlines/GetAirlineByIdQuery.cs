using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Application.Queries.Admin.Airlines;
using MediatR;

namespace FlightAssistant.Application.Queries.Admin.Airlines
{
    public record GetAirlineByIdQuery(int Id) : IRequest<AirlineResult?>;

    public sealed class GetAirlineByIdQueryHandler : IRequestHandler<GetAirlineByIdQuery, AirlineResult?>
    {
        private readonly IAirlineRepository _repo;
        public GetAirlineByIdQueryHandler(IAirlineRepository repo) => _repo = repo;

        public async Task<AirlineResult?> Handle(GetAirlineByIdQuery request, CancellationToken ct)
        {
            var a = await _repo.GetByIdAsync(request.Id, ct);
            if (a is null) return null;
            return new AirlineResult(a.Id, a.Iata, a.Icao, a.Name, a.CountryCode, a.AllianceCode);
        }
    }
}
