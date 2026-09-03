using FlightAssistant.Domain.Flights.Entities;

namespace FlightAssistant.Application.Interfaces.Repositories
{
    public interface IAdminFlightRepository
    {
        Task<(IReadOnlyList<Flight> Items, int Total)> GetAllAsync(int page, int pageSize, CancellationToken ct = default);
        Task<bool> AnyForAirlineAsync(int airlineId, CancellationToken ct = default);
        Task<Flight?> GetByIntIdAsync(int id, CancellationToken ct = default);

        /// <summary>The flight for the admin drill-down (#322), read <c>AsNoTracking</c> — unlike
        /// <see cref="GetByIntIdAsync"/>, which stays tracked for <see cref="DeleteAsync"/>.</summary>
        Task<Flight?> GetDetailAsync(int id, CancellationToken ct = default);

        Task DeleteAsync(Flight flight, CancellationToken ct = default);
        Task SaveChangesAsync(CancellationToken ct = default);
    }
}
