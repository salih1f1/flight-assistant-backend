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
        /// Every flight the traveler tracks, joined server-side with its <see cref="Flight"/> row —
        /// the unpaged sibling of <see cref="GetTravelerFlightsAsync"/>, for the admin traveler
        /// drill-down (#322), which returns the whole relationship in one call rather than a page
        /// of it. A traveler's own tracked-flight count is bounded in practice (nothing like the
        /// unbounded growth <see cref="GetRecentFlightStatusEventsAsync"/> guards against), so no
        /// cap is applied here.
        /// </summary>
        Task<IReadOnlyList<(TravelerFlight Tracking, Flight Flight)>> GetAllTravelerFlightsAsync(Guid travelerId, CancellationToken ct = default);

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
        Task<IReadOnlyList<Flight>> GetActiveTrackedFlightsAsync(CancellationToken ct = default);
    }
}
