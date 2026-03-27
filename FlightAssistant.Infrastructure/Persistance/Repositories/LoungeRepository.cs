using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Lounges.Entities;
using Microsoft.EntityFrameworkCore;

namespace FlightAssistant.Infrastructure.Persistance.Repositories
{
    public class LoungeRepository : ILoungeRepository
    {
        private readonly FlightAssistantDbContext _db;
        public LoungeRepository(FlightAssistantDbContext db) => _db = db;

        // Generic IRepository<Lounge> — Lounge uses int PK
        public Task<Lounge?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
            throw new NotSupportedException("Lounge uses int PK. Use GetByIdWithProvidersAsync instead.");

        public async Task AddAsync(Lounge entity, CancellationToken ct = default) =>
            await _db.Lounges.AddAsync(entity, ct);

        public Task SaveChangesAsync(CancellationToken ct = default) =>
            _db.SaveChangesAsync(ct);

        // --- Domain-specific queries ---

        public async Task<IReadOnlyList<Lounge>> GetByAirportAsync(string airportIata, CancellationToken ct = default) =>
            await _db.Lounges
                .Where(l => _db.Airports.Any(a => a.Iata == airportIata && a.Id == l.AirportId))
                .Include(l => l.Providers)
                    .ThenInclude(link => link.Provider)
                .OrderBy(l => l.Name)
                .ToListAsync(ct);

        public Task<Lounge?> GetByIdWithProvidersAsync(int id, CancellationToken ct = default) =>
            _db.Lounges
                .Include(l => l.Providers)
                    .ThenInclude(link => link.Provider)
                .FirstOrDefaultAsync(l => l.Id == id, ct);
    }
}
