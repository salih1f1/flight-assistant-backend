using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Loyalty.Entities;
using Microsoft.EntityFrameworkCore;

namespace FlightAssistant.Infrastructure.Persistance.Repositories
{
    public class LoyaltyRepository : ILoyaltyRepository
    {
        private readonly FlightAssistantDbContext _db;
        public LoyaltyRepository(FlightAssistantDbContext db) => _db = db;

        public async Task AddAsync(LoyaltyProgram entity, CancellationToken ct = default) =>
            await _db.LoyaltyPrograms.AddAsync(entity, ct);

        public Task SaveChangesAsync(CancellationToken ct = default) =>
            _db.SaveChangesAsync(ct);

        // --- Domain-specific queries ---

        public async Task<(IReadOnlyList<LoyaltyProgram> Items, int Total)> GetProgramsAsync(int page, int pageSize, CancellationToken ct = default)
        {
            var query = _db.LoyaltyPrograms
                .Include(p => p.Tiers)
                .Include(p => p.Kind)
                .OrderBy(p => p.Name);
            var total = await query.CountAsync(ct);
            var items = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
            return (items, total);
        }

        public async Task<(IReadOnlyList<TravelerLoyaltyMembership> Items, int Total)> GetMembershipsForTravelerAsync(Guid travelerId, int page, int pageSize, CancellationToken ct = default)
        {
            var query = _db.TravelerLoyaltyMemberships
                .Where(m => m.TravelerId == travelerId);
            var total = await query.CountAsync(ct);
            var items = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
            return (items, total);
        }

        public Task<TravelerLoyaltyMembership?> GetMembershipByIdAsync(Guid id, CancellationToken ct = default) =>
            _db.TravelerLoyaltyMemberships.FirstOrDefaultAsync(m => m.Id == id, ct);

        public async Task AddMembershipAsync(TravelerLoyaltyMembership membership, CancellationToken ct = default) =>
            await _db.TravelerLoyaltyMemberships.AddAsync(membership, ct);

        public Task RemoveMembershipAsync(TravelerLoyaltyMembership membership, CancellationToken ct = default)
        {
            _db.TravelerLoyaltyMemberships.Remove(membership);
            return Task.CompletedTask;
        }

        public Task<LoyaltyProgram?> GetProgramByIdAsync(int id, CancellationToken ct = default) =>
            _db.LoyaltyPrograms
                .Include(p => p.Tiers)
                .Include(p => p.Kind)
                .FirstOrDefaultAsync(p => p.Id == id, ct);

        public async Task AddProgramAsync(LoyaltyProgram program, CancellationToken ct = default) =>
            await _db.LoyaltyPrograms.AddAsync(program, ct);

        public Task DeleteProgramAsync(LoyaltyProgram program, CancellationToken ct = default)
        {
            _db.LoyaltyPrograms.Remove(program);
            return Task.CompletedTask;
        }
    }
}
