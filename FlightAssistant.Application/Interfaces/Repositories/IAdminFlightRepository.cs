using FlightAssistant.Domain.Flights.Entities;

namespace FlightAssistant.Application.Interfaces.Repositories
{
    public interface IAdminFlightRepository
    {
        Task<IReadOnlyList<Flight>> GetAllAsync(CancellationToken ct = default);
        Task<Flight?> GetByIntIdAsync(int id, CancellationToken ct = default);
        Task DeleteAsync(Flight flight, CancellationToken ct = default);
        Task SaveChangesAsync(CancellationToken ct = default);
    }
}
