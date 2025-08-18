using FlightAssistant.Domain.Common;

namespace FlightAssistant.Domain.Travelers.Events
{
    public sealed record TravelerRegistered(
        Guid TravelerId,
        string Email,
        DateTime OccurredUtc
    ) : DomainEvent(OccurredUtc);
}
