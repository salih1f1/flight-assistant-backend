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

            await _push.SendAsync(tokens, title, body, ct);
        }
    }
}
