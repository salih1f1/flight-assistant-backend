using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.ReferenceData.Entities;
using MediatR;

namespace FlightAssistant.Application.Commands.Admin.Airports
{
    public record CreateAirportCommand(
        string Iata,
        string? Icao,
        string Name,
        string City,
        string CountryCode,
        double? Latitude,
        double? Longitude,
        int? ElevationFt,
        string? Timezone
    ) : IRequest<int>;

    public sealed class CreateAirportCommandHandler : IRequestHandler<CreateAirportCommand, int>
    {
        private readonly IAirportRepository _repo;
        public CreateAirportCommandHandler(IAirportRepository repo) => _repo = repo;

        public async Task<int> Handle(CreateAirportCommand cmd, CancellationToken ct)
        {
            var airport = Airport.Create(cmd.Iata, cmd.Icao, cmd.Name, cmd.City, cmd.CountryCode,
                cmd.Latitude, cmd.Longitude, cmd.ElevationFt, cmd.Timezone);
            await _repo.AddAsync(airport, ct);
            await _repo.SaveChangesAsync(ct);
            return airport.Id;
        }
    }
}
