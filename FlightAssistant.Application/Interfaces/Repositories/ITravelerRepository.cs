using FlightAssistant.Domain.Common.Interfaces;
using FlightAssistant.Domain.Travelers.Entities;

namespace FlightAssistant.Application.Interfaces.Repositories
{
    public interface ITravelerRepository : IRepository<Traveler>
    {
        Task<Traveler?> GetByEmailAsync(string email, CancellationToken ct = default);
    }
}
