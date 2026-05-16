using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Lounges.Entities;
using Microsoft.EntityFrameworkCore;

namespace FlightAssistant.Infrastructure.Persistance.Repositories
{
    public class LoungeRepository : ILoungeRepository
    {
        private readonly FlightAssistantDbContext _db;
        public LoungeRepository(FlightAssistantDbContext db) => _db = db;

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

        public async Task<(IReadOnlyList<Lounge> Items, int Total)> GetAllAsync(int page, int pageSize, CancellationToken ct = default)
        {
            var query = _db.Lounges
                .Include(l => l.Providers)
                    .ThenInclude(link => link.Provider)
                .OrderBy(l => l.Name);
            var total = await query.CountAsync(ct);
            var items = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
            return (items, total);
        }

        public Task DeleteAsync(Lounge lounge, CancellationToken ct = default)
        {
            _db.Lounges.Remove(lounge);
            return Task.CompletedTask;
        }

        public async Task<IReadOnlyList<Lounge>> GetAllVerifiedWithLinksAsync(CancellationToken ct = default) =>
            await _db.Lounges
                .Where(l => l.IsVerified)
                .Include(l => l.Providers)
                .OrderBy(l => l.Name)
                .ToListAsync(ct);

        public Task<Lounge?> FindByNaturalKeyWithProvidersAsync(int airportId, string name, string? terminal, CancellationToken ct = default) =>
            _db.Lounges
                .Include(l => l.Providers)
                .FirstOrDefaultAsync(l => l.AirportId == airportId && l.Name == name && l.Terminal == terminal, ct);

        public Task<LoungeProvider?> FindProviderByCodeAsync(string code, CancellationToken ct = default) =>
            _db.LoungeProviders.FirstOrDefaultAsync(p => p.Code == code, ct);

        public async Task AddProviderAsync(LoungeProvider provider, CancellationToken ct = default) =>
            await _db.LoungeProviders.AddAsync(provider, ct);
    }
}
