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
    }
}
