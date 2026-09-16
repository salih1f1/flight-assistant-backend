using FlightAssistant.Domain.Common;

namespace FlightAssistant.Domain.Flights.Events
{
    /// <summary>
    /// Raised by <see cref="Entities.Flight.MarkTracked"/> on every track request, whether the
    /// tracking link is new or the traveler already tracked this flight (#410, R1) — a repeat track
    /// can still find the airline or an airport newly unverified or stale. Carries only ids, never
    /// entities or verification state (R2), so the handler that reacts to it can decide what to check
    /// without re-reading the flight.
    /// </summary>
    public sealed record FlightTracked(
        int FlightId,
        int AirlineId,
        int DepartureAirportId,
        int ArrivalAirportId,
        DateTime OccurredUtc
    ) : DomainEvent(OccurredUtc);
}
