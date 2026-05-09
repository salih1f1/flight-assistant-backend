using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Flights.Events;
using MediatR;

namespace FlightAssistant.Application.Commands.Admin.Flights
{
    public record DeleteFlightAdminCommand(int FlightId) : IRequest;

    public sealed class DeleteFlightAdminCommandHandler : IRequestHandler<DeleteFlightAdminCommand>
    {
        private readonly IAdminFlightRepository _adminRepo;
        private readonly IFlightRepository _flightRepo;
        private readonly IPublisher _publisher;

        public DeleteFlightAdminCommandHandler(
            IAdminFlightRepository adminRepo,
            IFlightRepository flightRepo,
            IPublisher publisher)
        {
            _adminRepo = adminRepo;
            _flightRepo = flightRepo;
            _publisher = publisher;
        }

        public async Task Handle(DeleteFlightAdminCommand cmd, CancellationToken ct)
        {
            var flight = await _adminRepo.GetByIntIdAsync(cmd.FlightId, ct);
            if (flight is null) return;

            // Collect trackers before cascade delete removes TravelerFlight rows
            var trackings = await _flightRepo.GetTrackingsByFlightIdAsync(cmd.FlightId, ct);
            var trackerIds = trackings
                .Where(t => t.Notify)
                .Select(t => t.TravelerId)
                .ToList();

            await _adminRepo.DeleteAsync(flight, ct);
            await _adminRepo.SaveChangesAsync(ct);

            // Publish after commit — deleted entities are detached so the interceptor can't see them
            if (trackerIds.Count > 0)
                await _publisher.Publish(
                    new FlightDeleted(cmd.FlightId, flight.FlightNumber, trackerIds, DateTime.UtcNow), ct);
        }
    }
}
