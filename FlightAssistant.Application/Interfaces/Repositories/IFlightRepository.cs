using FlightAssistant.Domain.Common.Interfaces;
using FlightAssistant.Domain.Flights.Entities;

namespace FlightAssistant.Application.Interfaces.Repositories
{
    public interface IFlightRepository : IRepository<Flight>
    {
        Task<Flight?> GetByFlightNumberAndDateAsync(int airlineId, string flightNumber, DateOnly serviceDate, CancellationToken ct = default);

        /// <summary>
        /// Returns the existing flight matching the candidate's (AirlineId, FlightNumber, ServiceDateUtc),
        /// or inserts the candidate. Race-safe: if a concurrent caller inserts the same flight first,
        /// the unique-violation is caught and the winning row is returned.
        /// </summary>
        Task<Flight> GetOrCreateFlightAsync(Flight candidate, CancellationToken ct = default);
        Task<Flight?> GetFlightByIdAsync(int flightId, CancellationToken ct = default);
        Task<(IReadOnlyList<(TravelerFlight Tracking, Flight Flight)> Items, int Total)> GetTravelerFlightsAsync(Guid travelerId, int page, int pageSize, CancellationToken ct = default);

        /// <summary>
        /// The <paramref name="limit"/> most recently-serviced flights the traveler tracks, newest
        /// first, joined server-side with its <see cref="Flight"/> row — for the admin traveler
        /// drill-down (#322), which returns this relationship in one call rather than a page of it.
        /// Capped server-side (a <c>LIMIT</c> in the generated SQL, not a client-side <c>Take</c>
        /// after loading everything), mirroring <see cref="GetRecentFlightStatusEventsAsync"/>:
        /// <c>traveler_flights</c> has no retention/purge path and grows monotonically with usage
        /// (#322 fix round, M5), unlike loyalty memberships (schema-bounded by
        /// <c>UNIQUE(TravelerId, ProgramId)</c>) or device tokens (small in practice), neither of
        /// which needs this treatment.
        /// </summary>
        Task<IReadOnlyList<(TravelerFlight Tracking, Flight Flight)>> GetRecentTravelerFlightsAsync(Guid travelerId, int limit, CancellationToken ct = default);

        Task<TravelerFlight?> GetTravelerFlightAsync(Guid travelerId, int flightId, CancellationToken ct = default);
        Task AddTravelerFlightAsync(TravelerFlight tf, CancellationToken ct = default);
        Task RemoveTravelerFlightAsync(TravelerFlight tf, CancellationToken ct = default);
        Task<IReadOnlyList<TravelerFlight>> GetTrackingsByFlightIdAsync(int flightId, CancellationToken ct = default);
        Task<IReadOnlyList<FlightStatusEvent>> GetFlightStatusEventsAsync(int flightId, CancellationToken ct = default);

        /// <summary>
        /// The <paramref name="limit"/> most recent <see cref="FlightStatusEvent"/> rows for the
        /// flight, newest first, capped server-side (a <c>LIMIT</c> in the generated SQL, not a
        /// client-side <c>Take</c> after loading everything) — for the admin flight drill-down
        /// (#322). Unlike <see cref="GetFlightStatusEventsAsync"/>, which returns the full,
        /// unbounded history: a flight tracked for weeks and polled every couple of minutes
        /// accumulates rows without bound, and this endpoint must not load all of them just to
        /// show the recent ones.
        /// </summary>
        Task<IReadOnlyList<FlightStatusEvent>> GetRecentFlightStatusEventsAsync(int flightId, int limit, CancellationToken ct = default);

        Task AddFlightStatusEventAsync(FlightStatusEvent evt, CancellationToken ct = default);
        Task<bool> FlightStatusEventExistsAsync(string dedupHash, CancellationToken ct = default);
        Task<int?> GetFlightStatusIdByCodeAsync(string code, CancellationToken ct = default);

        /// <summary>
        /// Ids of tracked flights whose <see cref="Flight.NextSyncDueUtc"/> is now or earlier, or that
        /// have no <see cref="Flight.SyncMode"/> (never planned), for
        /// <c>FlightStatusSyncBackgroundService</c> (#356). Unplanned flights come first, then oldest
        /// due. "Now" comes from the repository's <see cref="TimeProvider"/> on every call.
        /// </summary>
        Task<IReadOnlyList<int>> GetDueTrackedFlightIdsAsync(CancellationToken ct = default);

        /// <summary>Whether any traveler tracks the flight.</summary>
        Task<bool> IsFlightTrackedAsync(int flightId, CancellationToken ct = default);

        /// <summary>
        /// How many flights the traveler tracks that the status sync still polls as of
        /// <paramref name="nowUtc"/> (#380): every tracked flight that <c>FlightSyncPlanner</c> would not
        /// mark Stopped. A flight counts until <paramref name="stopAfterArrival"/> after an actual
        /// arrival or a cancellation, and, when no arrival is reported, until
        /// <paramref name="stopWithoutArrivalAfter"/> after its estimated (else scheduled) arrival.
        /// Computed from times and status, not the stored sync mode, and counted in SQL, since
        /// <c>traveler_flights</c> keeps every flight a traveler has ever tracked.
        /// </summary>
        Task<int> CountActiveTrackedFlightsAsync(
            Guid travelerId, DateTime nowUtc, TimeSpan stopAfterArrival, TimeSpan stopWithoutArrivalAfter,
            CancellationToken ct = default);
    }
}
