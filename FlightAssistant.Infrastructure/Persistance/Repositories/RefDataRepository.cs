using FlightAssistant.Application.Interfaces.Repositories;
using Microsoft.EntityFrameworkCore;

namespace FlightAssistant.Infrastructure.Persistance.Repositories
{
    public class RefDataRepository : IRefDataRepository
    {
        private readonly FlightAssistantDbContext _db;
        public RefDataRepository(FlightAssistantDbContext db) => _db = db;

        public Task<int?> GetAirlineIdByIataAsync(string iata, CancellationToken ct = default) =>
            _db.Airlines
                .Where(a => a.Iata == iata.ToUpperInvariant())
                .Select(a => (int?)a.Id)
                .FirstOrDefaultAsync(ct);

        public Task<int?> GetAirportIdByIataAsync(string iata, CancellationToken ct = default) =>
            _db.Airports
                .Where(a => a.Iata == iata.ToUpperInvariant())
                .Select(a => (int?)a.Id)
                .FirstOrDefaultAsync(ct);

        public Task<string?> GetAirlineIataByIdAsync(int id, CancellationToken ct = default) =>
            _db.Airlines
                .Where(a => a.Id == id)
                .Select(a => (string?)a.Iata)
                .FirstOrDefaultAsync(ct);

        public Task<string?> GetAirportIataByIdAsync(int id, CancellationToken ct = default) =>
            _db.Airports
                .Where(a => a.Id == id)
                .Select(a => (string?)a.Iata)
                .FirstOrDefaultAsync(ct);
    }
}
