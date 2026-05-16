using FlightAssistant.Domain.ReferenceData.Entities;

namespace FlightAssistant.Application.Interfaces.Repositories
{
    public interface IAllianceRepository
    {
        Task<IReadOnlyList<Alliance>> GetAllAsync(CancellationToken ct = default);
        Task<Alliance?> FindByCodeAsync(string code, CancellationToken ct = default);
        Task AddAsync(Alliance alliance, CancellationToken ct = default);
        Task SaveChangesAsync(CancellationToken ct = default);
    }
}
