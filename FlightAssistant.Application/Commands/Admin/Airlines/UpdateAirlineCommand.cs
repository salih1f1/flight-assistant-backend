using FlightAssistant.Application.Interfaces.Repositories;
using MediatR;

namespace FlightAssistant.Application.Commands.Admin.Airlines
{
    public record UpdateAirlineCommand(
        int Id,
        string? Iata,
        string? Icao,
        string Name,
        string CountryCode,
        string? AllianceCode
    ) : IRequest;

    public sealed class UpdateAirlineCommandHandler : IRequestHandler<UpdateAirlineCommand>
    {
        private readonly IAirlineRepository _repo;
        public UpdateAirlineCommandHandler(IAirlineRepository repo) => _repo = repo;

        public async Task Handle(UpdateAirlineCommand cmd, CancellationToken ct)
        {
            var airline = await _repo.GetByIdAsync(cmd.Id, ct)
                ?? throw new KeyNotFoundException($"Airline {cmd.Id} not found.");
            airline.Update(cmd.Iata, cmd.Icao, cmd.Name, cmd.CountryCode, cmd.AllianceCode);
            await _repo.SaveChangesAsync(ct);
        }
    }
}
