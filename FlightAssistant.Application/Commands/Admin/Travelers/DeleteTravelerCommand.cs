using FlightAssistant.Application.Interfaces.Repositories;
using MediatR;

namespace FlightAssistant.Application.Commands.Admin.Travelers
{
    public record DeleteTravelerCommand(Guid TravelerId) : IRequest;

    public sealed class DeleteTravelerCommandHandler : IRequestHandler<DeleteTravelerCommand>
    {
        private readonly IAdminTravelerRepository _repo;
        public DeleteTravelerCommandHandler(IAdminTravelerRepository repo) => _repo = repo;

        public async Task Handle(DeleteTravelerCommand cmd, CancellationToken ct)
        {
            var traveler = await _repo.GetByIdAsync(cmd.TravelerId, ct);
            if (traveler is null) return;
            await _repo.DeleteAsync(traveler, ct);
            await _repo.SaveChangesAsync(ct);
        }
    }
}
