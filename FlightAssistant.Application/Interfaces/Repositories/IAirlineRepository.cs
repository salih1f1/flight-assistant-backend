using FlightAssistant.Domain.ReferenceData.Entities;

namespace FlightAssistant.Application.Interfaces.Repositories
{
    public interface IAirlineRepository
    {
        Task<IReadOnlyList<Airline>> GetAllAsync(CancellationToken ct = default);
        Task<Airline?> GetByIdAsync(int id, CancellationToken ct = default);
        Task AddAsync(Airline airline, CancellationToken ct = default);
        Task DeleteAsync(Airline airline, CancellationToken ct = default);
        Task SaveChangesAsync(CancellationToken ct = default);
    }
}
