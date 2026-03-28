using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Travelers.Entities;
using Microsoft.EntityFrameworkCore;

namespace FlightAssistant.Infrastructure.Persistance.Repositories
{
    public class AdminTravelerRepository : IAdminTravelerRepository
    {
        private readonly FlightAssistantDbContext _db;
        public AdminTravelerRepository(FlightAssistantDbContext db) => _db = db;

        public async Task<IReadOnlyList<Traveler>> GetAllAsync(CancellationToken ct = default) =>
            await _db.Travelers
                .Include(t => t.DeviceTokens)
                .OrderBy(t => t.Email)
                .ToListAsync(ct);

        public Task<Traveler?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
            _db.Travelers.Include(t => t.DeviceTokens).FirstOrDefaultAsync(t => t.Id == id, ct);

        public Task DeleteAsync(Traveler traveler, CancellationToken ct = default)
        {
            _db.Travelers.Remove(traveler);
            return Task.CompletedTask;
        }

        public Task SaveChangesAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);
    }
}
