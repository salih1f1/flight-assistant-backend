using FlightAssistant.Domain.ReferenceData.Entities;

namespace FlightAssistant.Application.Interfaces.Repositories
{
    public interface IAllianceRepository
    {
        Task<IReadOnlyList<Alliance>> GetAllAsync(CancellationToken ct = default);
    }
}
