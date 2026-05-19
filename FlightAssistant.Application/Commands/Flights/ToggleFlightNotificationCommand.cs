using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Common.Exceptions;
using MediatR;

namespace FlightAssistant.Application.Commands.Flights
{
    public record ToggleFlightNotificationCommand(Guid TravelerId, int FlightId, bool Notify) : IRequest;

    public sealed class ToggleFlightNotificationCommandHandler : IRequestHandler<ToggleFlightNotificationCommand>
    {
        private readonly IFlightRepository _flights;

        public ToggleFlightNotificationCommandHandler(IFlightRepository flights) => _flights = flights;

        public async Task Handle(ToggleFlightNotificationCommand cmd, CancellationToken ct)
        {
            var tf = await _flights.GetTravelerFlightAsync(cmd.TravelerId, cmd.FlightId, ct)
                ?? throw new DomainException($"Traveler is not tracking flight {cmd.FlightId}.", 404);

            tf.SetNotify(cmd.Notify);
            await _flights.SaveChangesAsync(ct);
        }
    }
}
