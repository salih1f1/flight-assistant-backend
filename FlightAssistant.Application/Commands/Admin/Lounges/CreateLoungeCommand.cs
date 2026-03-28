using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Lounges.Entities;
using MediatR;

namespace FlightAssistant.Application.Commands.Admin.Lounges
{
    public record CreateLoungeCommand(
        int AirportId,
        string Name,
        string? Terminal,
        string? LocationHint,
        string? Hours,
        bool IsAirside,
        bool IsDomesticOnly,
        bool IsInternationalOnly,
        string? Amenities
    ) : IRequest<int>;

    public sealed class CreateLoungeCommandHandler : IRequestHandler<CreateLoungeCommand, int>
    {
        private readonly ILoungeRepository _repo;
        public CreateLoungeCommandHandler(ILoungeRepository repo) => _repo = repo;

        public async Task<int> Handle(CreateLoungeCommand cmd, CancellationToken ct)
        {
            var lounge = Lounge.Create(cmd.AirportId, cmd.Name, cmd.Terminal, cmd.LocationHint,
                cmd.Hours, cmd.IsAirside, cmd.IsDomesticOnly, cmd.IsInternationalOnly, cmd.Amenities);
            await _repo.AddAsync(lounge, ct);
            await _repo.SaveChangesAsync(ct);
            return lounge.Id;
        }
    }
}
