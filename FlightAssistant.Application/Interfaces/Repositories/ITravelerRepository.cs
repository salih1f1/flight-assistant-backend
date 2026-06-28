using FlightAssistant.Domain.Common.Interfaces;
using FlightAssistant.Domain.Travelers.Entities;

namespace FlightAssistant.Application.Interfaces.Repositories
{
    public interface ITravelerRepository : IRepository<Traveler>
    {
        Task<Traveler?> GetByIdAsync(Guid id, CancellationToken ct = default);
        Task<IReadOnlyList<Traveler>> GetByIdsAsync(IReadOnlyList<Guid> ids, CancellationToken ct = default);
    }
}
