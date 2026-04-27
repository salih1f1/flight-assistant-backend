using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Travelers.Entities;
using Microsoft.EntityFrameworkCore;

namespace FlightAssistant.Infrastructure.Persistance.Repositories
{
    public class AdminTravelerRepository : IAdminTravelerRepository
    {
        private readonly FlightAssistantDbContext _db;
        public AdminTravelerRepository(FlightAssistantDbContext db) => _db = db;

        public async Task<(IReadOnlyList<Traveler> Items, int Total)> GetAllAsync(int page, int pageSize, CancellationToken ct = default)
        {
            var query = _db.Travelers
                .Include(t => t.DeviceTokens)
                .OrderBy(t => t.Email);
            var total = await query.CountAsync(ct);
            var items = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
            return (items, total);
        }

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
