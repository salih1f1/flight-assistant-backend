using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Loyalty.Entities;
using Microsoft.EntityFrameworkCore;

namespace FlightAssistant.Infrastructure.Persistance.Repositories
{
    public class LoyaltyRepository : ILoyaltyRepository
    {
        private readonly FlightAssistantDbContext _db;
        public LoyaltyRepository(FlightAssistantDbContext db) => _db = db;

        // Generic IRepository<LoyaltyProgram> — LoyaltyProgram uses int PK
        public Task<LoyaltyProgram?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
            throw new NotSupportedException("LoyaltyProgram uses int PK. Use GetProgramsAsync instead.");

        public async Task AddAsync(LoyaltyProgram entity, CancellationToken ct = default) =>
            await _db.LoyaltyPrograms.AddAsync(entity, ct);

        public Task SaveChangesAsync(CancellationToken ct = default) =>
            _db.SaveChangesAsync(ct);

        // --- Domain-specific queries ---

        public async Task<IReadOnlyList<LoyaltyProgram>> GetProgramsAsync(CancellationToken ct = default) =>
            await _db.LoyaltyPrograms
                .Include(p => p.Tiers)
                .Include(p => p.Kind)
                .OrderBy(p => p.Name)
                .ToListAsync(ct);

        public async Task<IReadOnlyList<TravelerLoyaltyMembership>> GetMembershipsForTravelerAsync(Guid travelerId, CancellationToken ct = default) =>
            await _db.TravelerLoyaltyMemberships
                .Where(m => m.TravelerId == travelerId)
                .ToListAsync(ct);

        public Task<TravelerLoyaltyMembership?> GetMembershipByIdAsync(Guid id, CancellationToken ct = default) =>
            _db.TravelerLoyaltyMemberships.FirstOrDefaultAsync(m => m.Id == id, ct);

        public async Task AddMembershipAsync(TravelerLoyaltyMembership membership, CancellationToken ct = default) =>
            await _db.TravelerLoyaltyMemberships.AddAsync(membership, ct);

        public Task RemoveMembershipAsync(TravelerLoyaltyMembership membership, CancellationToken ct = default)
        {
            _db.TravelerLoyaltyMemberships.Remove(membership);
            return Task.CompletedTask;
        }
    }
}
