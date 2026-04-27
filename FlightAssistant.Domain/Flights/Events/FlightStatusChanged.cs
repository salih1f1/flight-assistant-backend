using FlightAssistant.Domain.Common;

namespace FlightAssistant.Domain.Flights.Events
{
    /// <summary>
    /// Raised by <see cref="Entities.Flight.ApplyStatusUpdate"/> when a new status event
    /// is applied to a flight. Handled by the push notification handler after SaveChanges
    /// succeeds via the DomainEventDispatchInterceptor.
    /// </summary>
    public sealed record FlightStatusChanged(
        int FlightId,
        string FlightNumber,
        DateOnly ServiceDateUtc,
        string StatusCode,
        DateTime OccurredUtc
    ) : DomainEvent(OccurredUtc);
}
