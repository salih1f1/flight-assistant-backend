using FlightAssistant.Application.Interfaces.Notifications;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Flights.Events;
using MediatR;
using Microsoft.Extensions.Logging;

namespace FlightAssistant.Application.EventHandlers
{
    public sealed class FlightDeletedEventHandler : INotificationHandler<FlightDeleted>
    {
        private readonly ITravelerRepository _travelers;
        private readonly IPushNotificationService _push;
        private readonly ILogger<FlightDeletedEventHandler> _logger;

        public FlightDeletedEventHandler(
            ITravelerRepository travelers,
            IPushNotificationService push,
            ILogger<FlightDeletedEventHandler> logger)
        {
            _travelers = travelers;
            _push = push;
            _logger = logger;
        }

        public async Task Handle(FlightDeleted notification, CancellationToken ct)
        {
            var title = $"Flight {notification.FlightNumber} Removed";
            var body = $"Flight {notification.FlightNumber} is no longer available and has been removed from your tracked flights.";

            var travelerIds = notification.TrackerTravelerIds.Distinct().ToList();
            var travelers = await _travelers.GetByIdsAsync(travelerIds, ct);

            var tokens = new List<string>();
            foreach (var traveler in travelers)
                tokens.AddRange(traveler.DeviceTokens.Select(d => d.Token));

            if (tokens.Count == 0) return;

            _logger.LogInformation(
                "Sending {Count} push notifications for deleted flight {FlightNumber}",
                tokens.Count, notification.FlightNumber);

            var invalidTokens = await _push.SendAsync(tokens, title, body, ct);

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
    }
}
