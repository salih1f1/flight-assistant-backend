using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.ReferenceData.Entities;
using MediatR;

namespace FlightAssistant.Application.Commands.Admin.Airlines
{
    public record CreateAirlineCommand(
        string? Iata,
        string? Icao,
        string Name,
        string CountryCode,
        string? AllianceCode
    ) : IRequest<int>;

    public sealed class CreateAirlineCommandHandler : IRequestHandler<CreateAirlineCommand, int>
    {
        private readonly IAirlineRepository _repo;
        public CreateAirlineCommandHandler(IAirlineRepository repo) => _repo = repo;

        public async Task<int> Handle(CreateAirlineCommand cmd, CancellationToken ct)
        {
            var airline = Airline.Create(cmd.Iata, cmd.Icao, cmd.Name, cmd.CountryCode, cmd.AllianceCode);
            await _repo.AddAsync(airline, ct);
            await _repo.SaveChangesAsync(ct);
            return airline.Id;
        }
    }
}
