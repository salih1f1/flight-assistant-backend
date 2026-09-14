using FlightAssistant.Application.Interfaces.Repositories;
using FluentValidation;
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

            // Last tracker gone: nothing syncs the flight any more. LastSyncedUtc is kept (spec §2.5).
            // The unsaved removal can still come back from the query, so ignore it by id.
            var trackings = await _flights.GetTrackingsByFlightIdAsync(cmd.FlightId, ct);
            if (trackings.All(t => t.Id == tf.Id))
            {
                var flight = await _flights.GetFlightByIdAsync(cmd.FlightId, ct);
                flight?.ApplySyncPlan(null, null);
            }

            await _flights.SaveChangesAsync(ct);
        }
    }

    public sealed class UntrackFlightCommandValidator : AbstractValidator<UntrackFlightCommand>
    {
        public UntrackFlightCommandValidator()
        {
            RuleFor(x => x.FlightId).GreaterThan(0);
        }
    }
}
