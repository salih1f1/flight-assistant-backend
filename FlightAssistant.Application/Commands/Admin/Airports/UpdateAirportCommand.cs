using FlightAssistant.Application.Interfaces.Repositories;
using MediatR;

namespace FlightAssistant.Application.Commands.Admin.Airports
{
    public record UpdateAirportCommand(
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
    ) : IRequest;

    public sealed class UpdateAirportCommandHandler : IRequestHandler<UpdateAirportCommand>
    {
        private readonly IAirportRepository _repo;
        public UpdateAirportCommandHandler(IAirportRepository repo) => _repo = repo;

        public async Task Handle(UpdateAirportCommand cmd, CancellationToken ct)
        {
            var airport = await _repo.GetByIdAsync(cmd.Id, ct)
                ?? throw new KeyNotFoundException($"Airport {cmd.Id} not found.");
            airport.Update(cmd.Iata, cmd.Icao, cmd.Name, cmd.City, cmd.CountryCode,
                cmd.Latitude, cmd.Longitude, cmd.ElevationFt, cmd.Timezone);
            await _repo.SaveChangesAsync(ct);
        }
    }
}
