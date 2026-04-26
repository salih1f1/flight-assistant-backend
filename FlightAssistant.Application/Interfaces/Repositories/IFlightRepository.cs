using FlightAssistant.Domain.Common.Interfaces;
using FlightAssistant.Domain.Flights.Entities;

namespace FlightAssistant.Application.Interfaces.Repositories
{
    public interface IFlightRepository : IRepository<Flight>
    {
        Task<Flight?> GetByFlightNumberAndDateAsync(int airlineId, string flightNumber, DateOnly serviceDate, CancellationToken ct = default);
        Task<Flight?> GetFlightByIdAsync(int flightId, CancellationToken ct = default);
        Task<IReadOnlyList<(TravelerFlight Tracking, Flight Flight)>> GetTravelerFlightsAsync(Guid travelerId, CancellationToken ct = default);
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
