using FlightAssistant.Domain.Common;

namespace FlightAssistant.Domain.Travelers.Events
{
    public sealed record DeviceTokenAdded(
        Guid TravelerId,
        Guid DeviceTokenId,
        string Platform,
        string Token,
        DateTime OccurredUtc
    ) : DomainEvent(OccurredUtc);
}
