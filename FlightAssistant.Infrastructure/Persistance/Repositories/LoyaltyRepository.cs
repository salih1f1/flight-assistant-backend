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
                // Id tiebreaker: Name is not unique, and paging on a tie can repeat or skip rows (#328).
                .OrderBy(p => p.Name)
                .ThenBy(p => p.Id);
            var total = await query.CountAsync(ct);
            var items = await query.Page(page, pageSize).ToListAsync(ct);
            return (items, total);
        }

        public async Task<(IReadOnlyList<TravelerLoyaltyMembership> Items, int Total)> GetMembershipsForTravelerAsync(Guid travelerId, int page, int pageSize, CancellationToken ct = default)
        {
            var query = _db.TravelerLoyaltyMemberships
                .Where(m => m.TravelerId == travelerId)
                // Same order as GetAllMembershipsForTravelerAsync below. UNIQUE(TravelerId, ProgramId)
                // makes ProgramId unique within this filter, so pages can't overlap or skip rows (#328).
                .OrderBy(m => m.ProgramId);
            var total = await query.CountAsync(ct);
            var items = await query.Page(page, pageSize).ToListAsync(ct);
            return (items, total);
        }

        public async Task<IReadOnlyList<TravelerLoyaltyMembership>> GetAllMembershipsForTravelerAsync(Guid travelerId, CancellationToken ct = default) =>
            await _db.TravelerLoyaltyMemberships
                .AsNoTracking()
                .Where(m => m.TravelerId == travelerId)
                // Deterministic order: without one, Postgres row order can churn between identical
                // calls after an unrelated UPDATE (#322 fix round, M4) — ProgramId ascending is
                // arbitrary but stable, matching tracked flights' own ORDER BY sibling.
                .OrderBy(m => m.ProgramId)
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

        public Task<IReadOnlyList<LoyaltyProgram>> SearchProgramsByAirlineIdAsync(int airlineId, CancellationToken ct = default) =>
            _db.LoyaltyPrograms
                .Include(p => p.Tiers)
                .Include(p => p.Kind)
                .Where(p => p.AirlineId == airlineId)
                .OrderBy(p => p.Name)
                .ToListAsync(ct)
                .ContinueWith(t => (IReadOnlyList<LoyaltyProgram>)t.Result, ct, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        public async Task<IReadOnlyDictionary<int, string>> GetProgramNamesByAirlineIdsAsync(IReadOnlyCollection<int> airlineIds, CancellationToken ct = default)
        {
            if (airlineIds.Count == 0)
                return new Dictionary<int, string>();

            // Only the two columns the caller names rows by: no Include, since neither the tiers
            // nor the kind are part of the answer, and one IN (...) rather than a query per airline.
            // The AirlineId null check is what lets .Value read without a null-forgiving operator;
            // it is not what keeps alliance-wide programs out of the result, which SQL does on its
            // own — NULL IN (...) is unknown, not true. Deleting it leaves the tests green.
            var rows = await _db.LoyaltyPrograms
                .AsNoTracking()
                .Where(p => p.AirlineId != null && airlineIds.Contains(p.AirlineId.Value))
                .OrderBy(p => p.Name)
                // Id tiebreaker: Name is not unique, and "first by name" has to name the same row
                // twice running (#328's rule, for a group rather than a page).
                .ThenBy(p => p.Id)
                .Select(p => new { AirlineId = p.AirlineId!.Value, p.Name })
                .ToListAsync(ct);

            // First per airline, grouped here rather than in SQL: the row count is one program per
            // airline for all but a handful, and DISTINCT ON has no provider-portable translation.
            return rows
                .GroupBy(r => r.AirlineId)
                .ToDictionary(g => g.Key, g => g.First().Name);
        }

        public async Task<IReadOnlyList<LoyaltyProgram>> GetAllVerifiedWithTiersAsync(CancellationToken ct = default) =>
            await _db.LoyaltyPrograms
                .Where(p => p.IsVerified)
                .Include(p => p.Tiers)
                .OrderBy(p => p.Name)
                .ToListAsync(ct);

        public async Task<IReadOnlyDictionary<int, string>> GetKindCodeMapAsync(CancellationToken ct = default) =>
            await _db.LoyaltyProgramKinds
                .ToDictionaryAsync(k => k.Id, k => k.Code, ct);

        public Task<LoyaltyProgramKind?> FindKindByCodeAsync(string code, CancellationToken ct = default) =>
            _db.LoyaltyProgramKinds.FirstOrDefaultAsync(k => k.Code == code, ct);

        public Task<LoyaltyProgramKind?> FindKindByIdAsync(int id, CancellationToken ct = default) =>
            _db.LoyaltyProgramKinds.FirstOrDefaultAsync(k => k.Id == id, ct);

        public async Task AddKindAsync(LoyaltyProgramKind kind, CancellationToken ct = default) =>
            await _db.LoyaltyProgramKinds.AddAsync(kind, ct);

        public Task<LoyaltyProgram?> FindProgramByCodeWithTiersAsync(string code, CancellationToken ct = default) =>
            _db.LoyaltyPrograms
                .Include(p => p.Tiers)
                .FirstOrDefaultAsync(p => p.Code == code, ct);
    }
}
