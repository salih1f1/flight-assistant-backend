using FlightAssistant.Application.Interfaces.Notifications;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Flights.Events;
using MediatR;
using Microsoft.Extensions.Logging;

namespace FlightAssistant.Application.EventHandlers
{
    /// <summary>
    /// Sends push notifications to travelers who track a flight whenever
    /// a <see cref="FlightStatusChanged"/> domain event is published.
    /// The event is raised by <see cref="Domain.Flights.Entities.Flight.ApplyStatusUpdate"/>
    /// and dispatched by the EF SaveChanges interceptor after a successful commit.
    /// </summary>
    public sealed class FlightStatusChangedNotificationHandler : INotificationHandler<FlightStatusChanged>
    {
        private readonly IFlightRepository _flights;
        private readonly ITravelerRepository _travelers;
        private readonly IPushNotificationService _push;
        private readonly ILogger<FlightStatusChangedNotificationHandler> _logger;

        public FlightStatusChangedNotificationHandler(
            IFlightRepository flights,
            ITravelerRepository travelers,
            IPushNotificationService push,
            ILogger<FlightStatusChangedNotificationHandler> logger)
        {
            _flights = flights;
            _travelers = travelers;
            _push = push;
            _logger = logger;
        }

        public async Task Handle(FlightStatusChanged notification, CancellationToken ct)
        {
            // Find all travelers tracking this flight with notifications enabled
            var trackings = await _flights.GetTrackingsByFlightIdAsync(notification.FlightId, ct);
            var notifyTrackings = trackings.Where(t => t.Notify).ToList();
            if (!notifyTrackings.Any()) return;

            var title = $"Flight {notification.FlightNumber} Update";
            var body = GetStatusMessage(notification.StatusCode, notification.ServiceDateUtc);

            // Collect all device tokens from notified travelers
            var tokens = new List<string>();
            foreach (var tracking in notifyTrackings)
            {
                var traveler = await _travelers.GetByIdAsync(tracking.TravelerId, ct);
                if (traveler is not null)
                    tokens.AddRange(traveler.DeviceTokens.Select(d => d.Token));
            }

            if (tokens.Count == 0) return;

            _logger.LogInformation(
                "Sending {Count} push notifications for flight {FlightNumber} status {Status}",
                tokens.Count, notification.FlightNumber, notification.StatusCode);

            await _push.SendAsync(tokens, title, body, ct);
        }

        private static string GetStatusMessage(string statusCode, DateOnly serviceDate) => statusCode switch
        {
            "DEP" => "Your flight has departed.",
            "ARR" => "Your flight has landed.",
            "DLY" => "Your flight is delayed.",
            "CAN" => "Your flight has been cancelled.",
            "DIV" => "Your flight has been diverted.",
            _ => $"Status update for your flight on {serviceDate}."
        };
    }
}
