using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.ReferenceData.Entities;
using Microsoft.EntityFrameworkCore;

namespace FlightAssistant.Infrastructure.Persistance.Repositories
{
    public class AllianceRepository : IAllianceRepository
    {
        private readonly FlightAssistantDbContext _db;
        public AllianceRepository(FlightAssistantDbContext db) => _db = db;

        public async Task<IReadOnlyList<Alliance>> GetAllAsync(CancellationToken ct = default) =>
            await _db.Alliances.OrderBy(a => a.Name).ToListAsync(ct);

        public Task<Alliance?> FindByCodeAsync(string code, CancellationToken ct = default) =>
            _db.Alliances.FirstOrDefaultAsync(a => a.Code == code.ToUpperInvariant(), ct);

        public async Task AddAsync(Alliance alliance, CancellationToken ct = default) =>
            await _db.Alliances.AddAsync(alliance, ct);

        public Task SaveChangesAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);
    }
}
