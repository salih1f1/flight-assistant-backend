using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Lounges.Entities;

namespace FlightAssistant.Infrastructure.Persistance.Repositories
{
    public class LoungeRepository : ILoungeRepository
    {
        public Task AddAsync(Lounge entity, CancellationToken ct = default)
        {
            throw new NotImplementedException();
        }

        public Task<Lounge?> GetByIdAsync(Guid id, CancellationToken ct = default)
        {
            throw new NotImplementedException();
        }

        public Task SaveChangesAsync(CancellationToken ct = default)
        {
            throw new NotImplementedException();
        }
    }
}
