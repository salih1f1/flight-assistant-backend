using FlightAssistant.Domain.Common;

namespace FlightAssistant.Domain.Travelers.Events
{
    public sealed record DeviceTokenRemoved(
        Guid TravelerId,
        Guid DeviceTokenId,
        DateTime OccurredUtc
    ) : DomainEvent(OccurredUtc);
}
