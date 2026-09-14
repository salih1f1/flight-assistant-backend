using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.ReferenceData.Entities;
using Microsoft.EntityFrameworkCore;

namespace FlightAssistant.Infrastructure.Persistance.Repositories
{
    public class AirportRepository : IAirportRepository
    {
        private readonly FlightAssistantDbContext _db;
        public AirportRepository(FlightAssistantDbContext db) => _db = db;

        public async Task<(IReadOnlyList<Airport> Items, int Total)> GetAllAsync(int page, int pageSize, CancellationToken ct = default)
        {
            // Id tiebreaker: Name is not unique, and paging on a tie can repeat or skip rows (#328).
            var query = _db.Airports.OrderBy(a => a.Name).ThenBy(a => a.Id);
            var total = await query.CountAsync(ct);
            var items = await query.Page(page, pageSize).ToListAsync(ct);
            return (items, total);
        }

        public Task<Airport?> GetByIdAsync(int id, CancellationToken ct = default) =>
            _db.Airports.FirstOrDefaultAsync(a => a.Id == id, ct);

        public Task<Airport?> FindByIataAsync(string iata, CancellationToken ct = default) =>
            _db.Airports.FirstOrDefaultAsync(a => a.Iata == iata.ToUpperInvariant(), ct);

        public async Task AddAsync(Airport airport, CancellationToken ct = default) =>
            await _db.Airports.AddAsync(airport, ct);

        public Task DeleteAsync(Airport airport, CancellationToken ct = default)
        {
            _db.Airports.Remove(airport);
            return Task.CompletedTask;
        }

        public Task SaveChangesAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);
    }
}
