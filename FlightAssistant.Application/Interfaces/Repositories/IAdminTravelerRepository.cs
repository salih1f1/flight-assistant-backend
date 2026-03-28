using FlightAssistant.Domain.Travelers.Entities;

namespace FlightAssistant.Application.Interfaces.Repositories
{
    public interface IAdminTravelerRepository
    {
        Task<IReadOnlyList<Traveler>> GetAllAsync(CancellationToken ct = default);
        Task<Traveler?> GetByIdAsync(Guid id, CancellationToken ct = default);
        Task DeleteAsync(Traveler traveler, CancellationToken ct = default);
        Task SaveChangesAsync(CancellationToken ct = default);
    }
}
