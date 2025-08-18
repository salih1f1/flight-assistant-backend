using FlightAssistant.Domain.Common;

namespace FlightAssistant.Domain.Travelers.Events
{
    public sealed record TravelerProfileUpdated(
        Guid TravelerId,
        string? NewDisplayName,
        string? NewCountryCode,
        DateTime OccurredUtc
    ) : DomainEvent(OccurredUtc);
}
