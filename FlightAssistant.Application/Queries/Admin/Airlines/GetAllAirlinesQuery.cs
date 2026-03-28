using FlightAssistant.Application.Interfaces.Repositories;
using MediatR;

namespace FlightAssistant.Application.Queries.Admin.Airlines
{
    public record AirlineResult(
        int Id,
        string? Iata,
        string? Icao,
        string Name,
        string CountryCode,
        string? AllianceCode
    );

    public record GetAllAirlinesQuery : IRequest<IReadOnlyList<AirlineResult>>;

    public sealed class GetAllAirlinesQueryHandler : IRequestHandler<GetAllAirlinesQuery, IReadOnlyList<AirlineResult>>
    {
        private readonly IAirlineRepository _repo;
        public GetAllAirlinesQueryHandler(IAirlineRepository repo) => _repo = repo;

        public async Task<IReadOnlyList<AirlineResult>> Handle(GetAllAirlinesQuery request, CancellationToken ct)
        {
            var airlines = await _repo.GetAllAsync(ct);
            return airlines.Select(a => new AirlineResult(a.Id, a.Iata, a.Icao, a.Name, a.CountryCode, a.AllianceCode)).ToList();
        }
    }
}
