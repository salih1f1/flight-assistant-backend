using FlightAssistant.Application.Interfaces.Repositories;
using MediatR;

namespace FlightAssistant.Application.Commands.Flights
{
    public record UntrackFlightCommand(Guid TravelerId, int FlightId) : IRequest;

    public sealed class UntrackFlightCommandHandler : IRequestHandler<UntrackFlightCommand>
    {
        private readonly IFlightRepository _flights;

        public UntrackFlightCommandHandler(IFlightRepository flights) => _flights = flights;

        public async Task Handle(UntrackFlightCommand cmd, CancellationToken ct)
        {
            var tf = await _flights.GetTravelerFlightAsync(cmd.TravelerId, cmd.FlightId, ct);
            if (tf is null) return; // already untracked — idempotent

            await _flights.RemoveTravelerFlightAsync(tf, ct);
            await _flights.SaveChangesAsync(ct);
        }
    }
}
