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
            // A correction, or a delay that grew less than the threshold, records the event but sends
            // nothing (#352).
            if (!notification.ShouldNotify)
            {
                _logger.LogInformation(
                    "Skipping push for flight {FlightNumber}: status {Status} didn't change enough to notify",
                    notification.FlightNumber, notification.StatusCode);
                return;
            }

            // Find all travelers tracking this flight with notifications enabled
            var body = GetStatusMessage(notification.StatusCode, notification.DelayMinutes);

            // Suppress non-actionable status updates (e.g. SCH, UNK) — no meaningful
            // user-facing message, so skip before doing any tracking/traveler lookups.
            if (body is null)
            {
                _logger.LogInformation(
                    "Skipping push for flight {FlightNumber}: status {Status} is non-actionable",
                    notification.FlightNumber, notification.StatusCode);
                return;
            }

            var trackings = await _flights.GetTrackingsByFlightIdAsync(notification.FlightId, ct);
            var notifyTrackings = trackings.Where(t => t.Notify).ToList();
            if (!notifyTrackings.Any()) return;

            var title = $"Flight {notification.FlightNumber} Update";

            // Batch-load all travelers for the notified trackings to avoid N+1 queries
            var travelerIds = notifyTrackings.Select(t => t.TravelerId).Distinct().ToList();
            var travelers = await _travelers.GetByIdsAsync(travelerIds, ct);
            var travelerMap = travelers.ToDictionary(t => t.Id);

            // Collect all device tokens from notified travelers
            var tokens = new List<string>();
            foreach (var tracking in notifyTrackings)
            {
                if (travelerMap.TryGetValue(tracking.TravelerId, out var traveler))
                    tokens.AddRange(traveler.DeviceTokens.Select(d => d.Token));
            }

            if (tokens.Count == 0) return;

            _logger.LogInformation(
                "Sending {Count} push notifications for flight {FlightNumber} status {Status}",
                tokens.Count, notification.FlightNumber, notification.StatusCode);

            IReadOnlyCollection<string> invalidTokens = Array.Empty<string>();
            try
            {
                invalidTokens = await _push.SendAsync(tokens, title, body, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to send push notifications for flight {FlightNumber}", notification.FlightNumber);
            }

            await PruneInvalidTokensAsync(travelers, invalidTokens, ct);
        }

        private async Task PruneInvalidTokensAsync(
            IReadOnlyCollection<Domain.Travelers.Entities.Traveler> travelers,
            IReadOnlyCollection<string> invalidTokens,
            CancellationToken ct)
        {
            if (invalidTokens.Count == 0) return;

            try
            {
                var invalidSet = new HashSet<string>(invalidTokens);
                var removedAny = false;
                foreach (var traveler in travelers)
                {
                    var toRemove = traveler.DeviceTokens
                        .Where(dt => invalidSet.Contains(dt.Token))
                        .Select(dt => dt.Id)
                        .ToList();
                    foreach (var id in toRemove)
                    {
                        traveler.RemoveDeviceToken(id);
                        removedAny = true;
                    }
                }

                if (removedAny)
                    await _travelers.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to prune invalid device tokens");
            }
        }

        private static string? GetStatusMessage(string statusCode, int? delayMinutes) => statusCode switch
        {
            "DEP" => "Your flight has departed.",
            "ARR" => "Your flight has landed.",
            // Minutes late rather than a clock time, so the message needs no timezone or locale (#352).
            "DLY" => delayMinutes is { } minutes ? $"Your flight is delayed by {minutes} min." : "Your flight is delayed.",
            "CAN" => "Your flight has been cancelled.",
            "DIV" => "Your flight has been diverted.",
            _ => null
        };
    }
}
