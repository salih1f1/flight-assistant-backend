using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Common.Exceptions;
using MediatR;

namespace FlightAssistant.Application.Commands.Admin.Lounges
{
    public record UpdateLoungeCommand(
        int Id,
        string Name,
        string? Terminal,
        string? LocationHint,
        string? Hours,
        bool IsAirside,
        bool IsDomesticOnly,
        bool IsInternationalOnly,
        string? Amenities
    ) : IRequest;

    public sealed class UpdateLoungeCommandHandler : IRequestHandler<UpdateLoungeCommand>
    {
        private readonly ILoungeRepository _repo;
        public UpdateLoungeCommandHandler(ILoungeRepository repo) => _repo = repo;

        public async Task Handle(UpdateLoungeCommand cmd, CancellationToken ct)
        {
            var lounge = await _repo.GetByIdWithProvidersAsync(cmd.Id, ct)
                ?? throw new DomainException($"Lounge {cmd.Id} not found.", 404);
            lounge.Update(cmd.Name, cmd.Terminal, cmd.LocationHint, cmd.Hours,
                cmd.IsAirside, cmd.IsDomesticOnly, cmd.IsInternationalOnly, cmd.Amenities);
            await _repo.SaveChangesAsync(ct);
        }
    }
}
