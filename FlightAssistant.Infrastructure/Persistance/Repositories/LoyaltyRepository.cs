using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Loyalty.Entities;

namespace FlightAssistant.Infrastructure.Persistance.Repositories
{
    internal class LoyaltyRepository : ILoyaltyRepository
    {
        public Task AddAsync(LoyaltyProgram entity, CancellationToken ct = default)
        {
            throw new NotImplementedException();
        }

        public Task<LoyaltyProgram?> GetByIdAsync(Guid id, CancellationToken ct = default)
        {
            throw new NotImplementedException();
        }

        public Task SaveChangesAsync(CancellationToken ct = default)
        {
            throw new NotImplementedException();
        }
    }
}
