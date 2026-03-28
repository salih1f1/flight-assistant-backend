using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Flights.Entities;
using Microsoft.EntityFrameworkCore;

namespace FlightAssistant.Infrastructure.Persistance.Repositories
{
    public class AdminFlightRepository : IAdminFlightRepository
    {
        private readonly FlightAssistantDbContext _db;
        public AdminFlightRepository(FlightAssistantDbContext db) => _db = db;

        public async Task<IReadOnlyList<Flight>> GetAllAsync(CancellationToken ct = default) =>
            await _db.Flights
                .OrderByDescending(f => f.ServiceDateUtc)
                .ThenBy(f => f.FlightNumber)
                .ToListAsync(ct);

        public Task<Flight?> GetByIntIdAsync(int id, CancellationToken ct = default) =>
            _db.Flights.FirstOrDefaultAsync(f => f.Id == id, ct);

        public Task DeleteAsync(Flight flight, CancellationToken ct = default)
        {
            _db.Flights.Remove(flight);
            return Task.CompletedTask;
        }

        public Task SaveChangesAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);
    }
}
