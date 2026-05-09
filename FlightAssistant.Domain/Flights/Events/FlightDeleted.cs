using FlightAssistant.Domain.Common;

namespace FlightAssistant.Domain.Flights.Events
{
    public sealed record FlightDeleted(
        int FlightId,
        string FlightNumber,
        IReadOnlyList<Guid> TrackerTravelerIds,
        DateTime OccurredUtc
    ) : DomainEvent(OccurredUtc);
}
