using FlightAssistant.Application.Commands.Flights;
using FlightAssistant.Application.Interfaces.Notifications;
using FlightAssistant.Application.Interfaces.Repositories;
using MediatR;
using Microsoft.Extensions.Logging;

namespace FlightAssistant.Application.EventHandlers
{
    /// <summary>
    /// After a SyncFlightStatusCommand succeeds, sends push notifications
    /// to all travelers who track the flight and have Notify = true.
    ///
    /// Triggered by calling SendFlightStatusNotificationCommand from the sync endpoint.
    /// </summary>
    public record SendFlightStatusNotificationCommand(
        int FlightId,
        string StatusCode,
        string FlightNumber,
        DateOnly ServiceDate
    ) : IRequest;

    public sealed class SendFlightStatusNotificationCommandHandler : IRequestHandler<SendFlightStatusNotificationCommand>
    {
        private readonly IFlightRepository _flights;
        private readonly ITravelerRepository _travelers;
        private readonly IPushNotificationService _push;
        private readonly ILogger<SendFlightStatusNotificationCommandHandler> _logger;

        public SendFlightStatusNotificationCommandHandler(
            IFlightRepository flights,
            ITravelerRepository travelers,
            IPushNotificationService push,
            ILogger<SendFlightStatusNotificationCommandHandler> logger)
        {
            _flights = flights;
            _travelers = travelers;
            _push = push;
            _logger = logger;
        }

        public async Task Handle(SendFlightStatusNotificationCommand cmd, CancellationToken ct)
        {
            // Find all travelers tracking this flight with notifications enabled
            var trackings = await _flights.GetTrackingsByFlightIdAsync(cmd.FlightId, ct);
            var notifyTrackings = trackings.Where(t => t.Notify).ToList();
            if (!notifyTrackings.Any()) return;

            var title = $"Flight {cmd.FlightNumber} Update";
            var body = GetStatusMessage(cmd.StatusCode, cmd.ServiceDate);

            // Collect all device tokens from notified travelers
            var tokens = new List<string>();
            foreach (var tracking in notifyTrackings)
            {
                var traveler = await _travelers.GetByIdAsync(tracking.TravelerId, ct);
                if (traveler is not null)
                    tokens.AddRange(traveler.DeviceTokens.Select(d => d.Token));
            }

            if (tokens.Count == 0) return;

            _logger.LogInformation("Sending {Count} push notifications for flight {FlightNumber} status {Status}", tokens.Count, cmd.FlightNumber, cmd.StatusCode);
            await _push.SendAsync(tokens, title, body, ct);
        }

        private static string GetStatusMessage(string statusCode, DateOnly serviceDate) => statusCode switch
        {
            "DEP" => $"Your flight has departed.",
            "ARR" => $"Your flight has landed.",
            "DLY" => $"Your flight is delayed.",
            "CAN" => $"Your flight has been cancelled.",
            "DIV" => $"Your flight has been diverted.",
            _ => $"Status update for your flight on {serviceDate}."
        };
    }
}
