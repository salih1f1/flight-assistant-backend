using FlightAssistant.Application.Interfaces.Repositories;
using MediatR;

namespace FlightAssistant.Application.Commands.Admin.Flights
{
    public record DeleteFlightAdminCommand(int FlightId) : IRequest;

    public sealed class DeleteFlightAdminCommandHandler : IRequestHandler<DeleteFlightAdminCommand>
    {
        private readonly IAdminFlightRepository _repo;
        public DeleteFlightAdminCommandHandler(IAdminFlightRepository repo) => _repo = repo;

        public async Task Handle(DeleteFlightAdminCommand cmd, CancellationToken ct)
        {
            var flight = await _repo.GetByIntIdAsync(cmd.FlightId, ct);
            if (flight is null) return;
            await _repo.DeleteAsync(flight, ct);
            await _repo.SaveChangesAsync(ct);
        }
    }
}
