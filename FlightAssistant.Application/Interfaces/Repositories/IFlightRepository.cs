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
        Task<TravelerFlight?> GetTravelerFlightAsync(Guid travelerId, int flightId, CancellationToken ct = default);
        Task AddTravelerFlightAsync(TravelerFlight tf, CancellationToken ct = default);
        Task RemoveTravelerFlightAsync(TravelerFlight tf, CancellationToken ct = default);
        Task<IReadOnlyList<TravelerFlight>> GetTrackingsByFlightIdAsync(int flightId, CancellationToken ct = default);
        Task<IReadOnlyList<FlightStatusEvent>> GetFlightStatusEventsAsync(int flightId, CancellationToken ct = default);
        Task AddFlightStatusEventAsync(FlightStatusEvent evt, CancellationToken ct = default);
        Task<bool> FlightStatusEventExistsAsync(string dedupHash, CancellationToken ct = default);
        Task<int?> GetFlightStatusIdByCodeAsync(string code, CancellationToken ct = default);
        Task<IReadOnlyList<Flight>> GetActiveTrackedFlightsAsync(CancellationToken ct = default);
    }
}
