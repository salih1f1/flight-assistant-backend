using FlightAssistant.Domain.ReferenceData.Entities;

namespace FlightAssistant.Application.Interfaces.Repositories
{
    public interface IAirportRepository
    {
        Task<(IReadOnlyList<Airport> Items, int Total)> GetAllAsync(int page, int pageSize, CancellationToken ct = default);
        Task<Airport?> GetByIdAsync(int id, CancellationToken ct = default);
        Task AddAsync(Airport airport, CancellationToken ct = default);
        Task DeleteAsync(Airport airport, CancellationToken ct = default);
        Task SaveChangesAsync(CancellationToken ct = default);
    }
}
