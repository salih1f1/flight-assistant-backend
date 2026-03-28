using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.ReferenceData.Entities;
using Microsoft.EntityFrameworkCore;

namespace FlightAssistant.Infrastructure.Persistance.Repositories
{
    public class AirportRepository : IAirportRepository
    {
        private readonly FlightAssistantDbContext _db;
        public AirportRepository(FlightAssistantDbContext db) => _db = db;

        public async Task<IReadOnlyList<Airport>> GetAllAsync(CancellationToken ct = default) =>
            await _db.Airports.OrderBy(a => a.Name).ToListAsync(ct);

        public Task<Airport?> GetByIdAsync(int id, CancellationToken ct = default) =>
            _db.Airports.FirstOrDefaultAsync(a => a.Id == id, ct);

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
