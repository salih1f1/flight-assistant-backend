using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Travelers.Entities;
using Microsoft.EntityFrameworkCore;

namespace FlightAssistant.Infrastructure.Persistance.Repositories
{
    public class TravelerRepository : ITravelerRepository
    {
        private readonly FlightAssistantDbContext _db;
        public TravelerRepository(FlightAssistantDbContext db) => _db = db;

        public Task<Traveler?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
            _db.Travelers.Include(t => t.DeviceTokens).FirstOrDefaultAsync(t => t.Id == id, ct);

        public Task<Traveler?> GetByEmailAsync(string email, CancellationToken ct = default) =>
            _db.Travelers.Include(t => t.DeviceTokens).FirstOrDefaultAsync(t => t.Email == email.Trim().ToLowerInvariant(), ct);

        public async Task<IReadOnlyList<Traveler>> GetByIdsAsync(IReadOnlyList<Guid> ids, CancellationToken ct = default)
            => await _db.Travelers
                .Include(t => t.DeviceTokens)
                .Where(t => ids.Contains(t.Id))
                .ToListAsync(ct);

        public async Task AddAsync(Traveler entity, CancellationToken ct = default)
            => await _db.Travelers.AddAsync(entity, ct);

        public Task SaveChangesAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);
    }
}
