using FlightAssistant.Domain.Common.Interfaces;
using FlightAssistant.Domain.Flights.Entities;
using FlightAssistant.Domain.Flights.Enums;

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
        /// flight, most recently recorded first (#351), capped server-side (a <c>LIMIT</c> in the generated SQL, not a
        /// client-side <c>Take</c> after loading everything) — for the admin flight drill-down
        /// (#322). Unlike <see cref="GetFlightStatusEventsAsync"/>, which returns the full,
        /// unbounded history: a flight tracked for weeks and polled every couple of minutes
        /// accumulates rows without bound, and this endpoint must not load all of them just to
        /// show the recent ones.
        /// </summary>
        Task<IReadOnlyList<FlightStatusEvent>> GetRecentFlightStatusEventsAsync(int flightId, int limit, CancellationToken ct = default);

        /// <summary>The flight's most recently recorded status event (the highest id), or null when it has
        /// none (#351). Read-only (not tracked).</summary>
        Task<FlightStatusEvent?> GetLatestFlightStatusEventAsync(int flightId, CancellationToken ct = default);

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

        /// <summary>
        /// The same rule as <see cref="CountActiveTrackedFlightsAsync"/> across <b>every</b> traveler,
        /// counted once rather than per traveler: how many distinct flights the status loop is still
        /// polling right now (#503). Distinct, because one flight tracked by five travelers is polled
        /// once and costs once -- counting tracking links instead would inflate the denominator of
        /// every derived cost figure.
        /// </summary>
        Task<int> CountActiveTrackedFlightsAsync(
            DateTime nowUtc, TimeSpan stopAfterArrival, TimeSpan stopWithoutArrivalAfter,
            CancellationToken ct = default);

        /// <summary>
        /// Tracked flights grouped by their <b>stored</b> <see cref="FlightSyncMode"/> (#503), which
        /// is the composition that explains poll volume -- Fast polls every few minutes, Schedule
        /// once a day.
        ///
        /// <para>
        /// Deliberately the stored mode, not the recomputed one, and the two can disagree: a stored
        /// mode is at most one loop interval stale, and a flight tracked by an older build may have
        /// none at all (null). That is the right choice here precisely because this reports what the
        /// loop has actually been doing, which is what the spend was incurred on -- unlike the cap's
        /// count above, which must be authoritative and so recomputes from times. A caller comparing
        /// the two should expect a small difference, not treat it as a defect.
        /// </para>
        /// </summary>
        Task<IReadOnlyList<(FlightSyncMode? Mode, int Count)>> CountTrackedFlightsByStoredSyncModeAsync(
            CancellationToken ct = default);

        /// <summary>
        /// Status events recorded in the window, grouped by <c>Source</c>, with the number of
        /// distinct flights they belong to (#503) -- the yield side of the spend. Spend with no
        /// events is a loop polling flights nothing is happening to; spend concentrated on few
        /// flights is a different problem from spend spread thin.
        /// </summary>
        Task<IReadOnlyList<(string Source, int Events, int DistinctFlights)>> CountStatusEventsBySourceAsync(
            DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);
    }
}
