using FlightAssistant.Domain.Common;

namespace FlightAssistant.Domain.Flights.Events
{
    /// <summary>
    /// Raised by <see cref="Entities.Flight.ApplyStatusUpdate"/> when a new status event
    /// is applied to a flight. Handled by the push notification handler after SaveChanges
    /// succeeds via the DomainEventDispatchInterceptor.
    /// </summary>
    /// <param name="ShouldNotify">Whether travelers should get a push (#352): the status changed, or a
    /// delay grew by at least <c>Flight.DelayThreshold</c> since the last delay push.</param>
    /// <param name="DelayMinutes">For a delay with an estimated departure after the schedule, how many
    /// minutes late the estimate is; otherwise null.</param>
    public sealed record FlightStatusChanged(
        int FlightId,
        string FlightNumber,
        DateOnly ServiceDateUtc,
        string StatusCode,
        DateTime OccurredUtc,
        bool ShouldNotify = true,
        int? DelayMinutes = null
    ) : DomainEvent(OccurredUtc);
}
