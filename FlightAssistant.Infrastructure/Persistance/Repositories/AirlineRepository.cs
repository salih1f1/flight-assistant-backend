using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.ReferenceData.Entities;
using Microsoft.EntityFrameworkCore;

namespace FlightAssistant.Infrastructure.Persistance.Repositories
{
    public class AirlineRepository : IAirlineRepository
    {
        private readonly FlightAssistantDbContext _db;
        public AirlineRepository(FlightAssistantDbContext db) => _db = db;

        public async Task<(IReadOnlyList<Airline> Items, int Total)> GetAllAsync(int page, int pageSize, CancellationToken ct = default)
        {
            var query = _db.Airlines.OrderBy(a => a.Name);
            var total = await query.CountAsync(ct);
            var items = await query.Page(page, pageSize).ToListAsync(ct);
            return (items, total);
        }

        public Task<Airline?> GetByIdAsync(int id, CancellationToken ct = default) =>
            _db.Airlines.FirstOrDefaultAsync(a => a.Id == id, ct);

        public Task<Airline?> FindByIataAsync(string iata, CancellationToken ct = default) =>
            _db.Airlines.FirstOrDefaultAsync(a => a.Iata == iata.ToUpperInvariant(), ct);

        public Task<Airline?> FindByIcaoAsync(string icao, CancellationToken ct = default) =>
            _db.Airlines.FirstOrDefaultAsync(a => a.Icao == icao.ToUpperInvariant(), ct);

        public async Task AddAsync(Airline airline, CancellationToken ct = default) =>
            await _db.Airlines.AddAsync(airline, ct);

        public Task DeleteAsync(Airline airline, CancellationToken ct = default)
        {
            _db.Airlines.Remove(airline);
            return Task.CompletedTask;
        }

        public Task SaveChangesAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);
    }
}
