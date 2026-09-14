using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Flights.Entities;
using Microsoft.EntityFrameworkCore;

namespace FlightAssistant.Infrastructure.Persistance.Repositories
{
    public class AdminFlightRepository : IAdminFlightRepository
    {
        private readonly FlightAssistantDbContext _db;
        public AdminFlightRepository(FlightAssistantDbContext db) => _db = db;

        public async Task<(IReadOnlyList<Flight> Items, int Total)> GetAllAsync(int page, int pageSize, CancellationToken ct = default)
        {
            var query = _db.Flights
                .OrderByDescending(f => f.ServiceDateUtc)
                .ThenBy(f => f.FlightNumber)
                // Id tiebreaker: two airlines can share a FlightNumber on one date (#328).
                .ThenBy(f => f.Id);
            var total = await query.CountAsync(ct);
            var items = await query.Page(page, pageSize).ToListAsync(ct);
            return (items, total);
        }

        public Task<bool> AnyForAirlineAsync(int airlineId, CancellationToken ct = default) =>
            _db.Flights.AnyAsync(f => f.AirlineId == airlineId, ct);

        public Task<Flight?> GetByIntIdAsync(int id, CancellationToken ct = default) =>
            _db.Flights.FirstOrDefaultAsync(f => f.Id == id, ct);

        public Task<Flight?> GetDetailAsync(int id, CancellationToken ct = default) =>
            _db.Flights.AsNoTracking().FirstOrDefaultAsync(f => f.Id == id, ct);

        public Task DeleteAsync(Flight flight, CancellationToken ct = default)
        {
            _db.Flights.Remove(flight);
            return Task.CompletedTask;
        }

        public Task SaveChangesAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);
    }
}
